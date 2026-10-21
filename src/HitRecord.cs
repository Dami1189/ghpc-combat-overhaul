using System.Collections.Generic;
using GHPC;
using GHPC.Weapons;
using UnityEngine;

namespace GHPCHitCam
{
    using ShotInfo = GHPC.Weapons.ShotInfo;

    internal enum FragKind { Main, Spall }

    /// VentFire / FireBurst / HatchBlown / TurretOff: what the game itself shows of a fire or a cook-off (flames out of
    /// a hatch or a hole, a flame burst out of every open exit, a hatch blown open, the turret thrown off).
    internal enum EvKind { Story, ArmorPen, ArmorStop, Ricochet, Module, Crew, CrewHit, AmmoHit, Fire, AmmoFire, Explosion, Knockout, Kill, VentFire, FireBurst, HatchBlown, TurretOff, Breach }

    /// One body-part hit (drives the ragdoll; not listed in the UI).
    internal class CrewHitRef { public CrewInfo Crew; public GHPC.Equipment.DestructibleComponent Part; }

    /// One recorded position of a round / fragment. Positions are in target-local space.
    internal class PathPoint
    {
        public Vector3 W;          // world position when recorded (debug only)
        public Vector3 L;          // target-local position
        public bool HasL;
        public float Speed;        // m/s, <= 0 if unknown
        public bool Impact;        // came from penCheck (hit something)
        public bool Jet;           // HEAT jet segment
        public float Sim;          // synthesized simulation time (s), 0 = first impact on target
    }

    internal class Fragment
    {
        public int Id;
        public Fragment Parent;
        public ShotInfo Shot;
        public FragKind Kind;
        public float Power;        // RHA mm the fragment can still punch
        public float Caliber;
        public Vector3 StartL;
        public Vector3 StartW;
        public bool HasStart;
        public float StartSpeed;
        public readonly List<PathPoint> Points = new List<PathPoint>();
        public bool HitSomething;
        public bool Damaging;
        public bool Terminated;
        public float StartSim;
        public float EndSim;

        public Vector3 PositionAt(float sim, out bool visible)
        {
            visible = false;
            if (Points.Count == 0) return StartL;
            if (sim < Points[0].Sim)
            {
                if (HasStart && sim >= StartSim && Points[0].Sim > StartSim)
                {
                    visible = true;
                    return Vector3.Lerp(StartL, Points[0].L, Mathf.InverseLerp(StartSim, Points[0].Sim, sim));
                }
                return HasStart ? StartL : Points[0].L;
            }
            visible = true;
            for (int i = 1; i < Points.Count; i++)
            {
                if (sim <= Points[i].Sim)
                {
                    PathPoint a = Points[i - 1], b = Points[i];
                    float t = b.Sim > a.Sim ? (sim - a.Sim) / (b.Sim - a.Sim) : 1f;
                    return Vector3.Lerp(a.L, b.L, t);
                }
            }
            return Points[Points.Count - 1].L;
        }
    }

    internal class ArmorHit
    {
        public Fragment Frag;
        public string Name;
        public Vector3 L, NormalL, DirL;
        public float Los, Nominal, Pen, Angle;
        public float Energy;           // J delivered to the plate: rod 1/2 m v^2, HEAT jet ~ pen, HE coupled blast
        public float Speed;            // m/s at impact
        public float Caliber, RodRadius;   // m: shell diameter and the penetrator's own radius (sub-calibre rods)
        public float Tnt;              // kg TNT equivalent of the filler
        public bool Penetrated, Ricochet, Era, IsSpall, PreJet;
        // the drawn surface at the hit (the armour colliders are boxes that only roughly follow the hull: a grazing round
        // can strike the thin end face of a cover's box although it skims along the visible plate)
        public bool SurfDone, HasSurf;
        public Vector3 SurfPointL, SurfNormalL;
        public float SurfAngle;        // degrees between the round's path and that surface (0 = grazing)
        /// The struck armour piece itself, as the game models it (its collider mesh / box), in target-local space:
        /// a flat list of triangle corners. This is the real plate: its outline, curvature and edges.
        public Vector3[] PlateTris;
        // that plate's struck face, worked out once (it was three times per plate: ~16 ms each on a big casting)
        public bool FaceDone;
        public System.Collections.Generic.List<Vector3> FaceTris;
        public Vector3 FaceN, FaceAt;
        public float HoleRadius;
        public AmmoType.AmmoCategory Category;
        public float Sim;
    }

    internal class HitEvent
    {
        public EvKind Kind;
        public string Text;
        public string Subject;
        public Fragment Frag;      // fragment that caused it, if it happened inside a round update
        public Vector3 L;
        public bool HasPos;
        public float RealT;        // Time.time when it happened
        public float Sim = float.NaN; // filled by Timeline; PositiveInfinity = late event (aftermath)
        public float Before = -1, After = -1;
        public object Ref;         // DestructibleComponent / crew key / AmmoVisual / FlammableItem
        public int Severity;       // 0 info, 1 damage, 2 heavy, 3 critical/destroyed
        // what hit (for physical impulses)
        public float Speed, Power, Mass;
        public bool Spall, Jet, HasDir;
        public Vector3 DirL;
        public Vector3 Extent;     // half-size of the burning / exploding thing (target-local box), zero if unknown
    }

    internal class HitRecord
    {
        public int Id;
        public ShotInfo RootShot;
        public Unit Target;
        public string TargetName = "?";
        public string ShooterName = "?";
        public bool PlayerShot;
        public AmmoType Ammo;
        public float FirstImpactReal = -1;
        public float CreatedReal;
        public float LastActivityReal;
        public Matrix4x4 W2LAtImpact;
        public Matrix4x4 L2WAtImpact;
        public Fragment Main;
        public readonly List<Fragment> Fragments = new List<Fragment>();
        public readonly Dictionary<ShotInfo, Fragment> ByShot = new Dictionary<ShotInfo, Fragment>();
        public readonly List<HitEvent> Events = new List<HitEvent>();
        public readonly List<ArmorHit> ArmorHits = new List<ArmorHit>();
        public readonly HashSet<string> Dedupe = new HashSet<string>();
        public GhostSnapshot Snapshot;
        public bool Ready;          // enough data to start playback
        public bool Saved;
        public bool CookOffShown;      // its rack cooked off after the hit cam closed and got a follow-up replay
        public float InteriorEnd = 0.004f;
        public float CoreEnd = 0.003f;      // main round done inside the vehicle: gets most of the slow motion
        public float ApproachStart = -0.004f;
        public Vector3 EntryL, EntryDirL;
        public bool HasEntry;
        public string Title = "HIT";
        public Color TitleColor = Color.white;
        public bool TargetKilled, TargetKnockedOut;
        public float OutAt = -1f;       // Time.time it was first out of the fight (destroyed, knocked out, bailed out)

        public Vector3 ToLocal(Vector3 world)
        {
            if (Target != null) return Target.transform.InverseTransformPoint(world);
            return W2LAtImpact.MultiplyPoint3x4(world);
        }

        public Vector3 DirToLocal(Vector3 worldDir)
        {
            if (Target != null) return Target.transform.InverseTransformDirection(worldDir);
            return W2LAtImpact.MultiplyVector(worldDir);
        }

        public void AddEvent(HitEvent e)
        {
            Events.Add(e);
            LastActivityReal = Time.time;
            WorldVents.OnEvent(this, e);       // the hull's pressure in the world (flame out of the holes)
        }
    }
}
