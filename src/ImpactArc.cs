using System.Collections.Generic;
using GHPC;
using GHPC.PhysicsHelpers;
using GHPC.Utility;
using UnityEngine;

namespace GHPCHitCam
{
    /// first thing the loaded round would hit, from its real flight
    internal struct ArcResult
    {
        public bool Hit;              // something in range
        public Vector3 Point;         // where
        public Vector3 Dir;           // round's direction there
        public float Speed;           // m/s there
        public float Time;            // s of flight
        public float Path;            // m flown along the arc
        public float Pen;             // its pen rating there (LiveRound.CurrentPenRating rules)
        public bool Armed;            // HEAT: past its arming distance
        public PenResult Probe;       // what it met: armour (verdict) / ground / scenery / a friendly
        public Unit Unit;             // vehicle it hits (null: ground / scenery / nothing)
        public bool Moving;           // hit worked out in that vehicle's moving frame (it moves during the flight)
    }

    /// flies the loaded round like the game does and finds where it lands:
    /// - start: the muzzle, along the barrel (MuzzleIdentity.forward), velocity = MuzzleVelocity * forward + vehicle's own
    ///   velocity, speed x (1 + 0.0004 (ammo temp F - 70)) (WeaponSystem.Fire + LiveRound.Init); so the gun's current
    ///   superelevation / zeroing is in it, whatever range the FCS is set to
    /// - flight: BallisticEvaluatorRK4's own step, a = g - Coeff * SectionalArea * AirDensity / Mass * v|v|
    /// - each ~35 m step gets raycast against what rounds hit (LiveRoundLayerMask plus the "Raycast Only Groups" boxes
    ///   that turn a vehicle's armour colliders on); first hit is read with PenProbe from just in front of it
    /// - moving vehicles are left out of that march: for each one the arc is re-walked in its own moving frame
    ///   (arc point - its velocity x flight time), so the hit lands where it really would if fired now, shown on the
    ///   vehicle where it is now
    /// plain raycasts for the march (cheap), game's collider-group raycast only where something got hit. no allocations
    internal static class ImpactArc
    {
        const int MaxSteps = 240;
        static readonly Vector3[] _p = new Vector3[MaxSteps + 2];
        static readonly float[] _t = new float[MaxSteps + 2], _s = new float[MaxSteps + 2], _sp = new float[MaxSteps + 2];
        static int _n;
        /// vehicles moving faster than ~0.5 m/s (caller fills it every update) and their velocities
        public static readonly List<Unit> Moving = new List<Unit>();
        public static readonly List<Vector3> MovingVel = new List<Vector3>();
        static float _vMax;
        static AmmoType _ammo;
        public static int LastSteps { get; private set; }
        public static int LastRaycasts { get; private set; }

        public static bool March(Vector3 muzzle, Vector3 forward, Vector3 ownVelocity, AmmoType a, float maxPath, Unit ignore, Faction side, out ArcResult res)
        {
            res = default;
            _n = 0;
            LastSteps = 0;
            LastRaycasts = 0;
            if (a == null) return false;
            Vector3 v0 = a.MuzzleVelocity * forward.normalized + ownVelocity;
            float vMax = v0.magnitude;                  // LiveRound.MaxSpeed
            if (vMax < 1f) return false;
            float temp = 1f;
            try { temp = 1f + 0.0004f * (GHPC.World.WorldEnvironmentManager.AmmoTempFahrenheit - 70f); } catch { }
            Vector3 p = muzzle, v = v0 * temp;
            float k = PenProbe.DragK(a);
            Vector3 g = Physics.gravity;
            float dt = Mathf.Clamp(35f / vMax, 0.008f, 0.06f);
            int mask = PenProbe.Mask | (1 << CodeUtils.LAYER_INDEX_RAYCAST_ONLY_GROUPS);
            float t = 0f, s = 0f;
            _vMax = vMax;
            _ammo = a;
            Add(p, 0f, 0f, v.magnitude);
            for (int step = 0; step < MaxSteps; step++)
            {
                Vector3 p2 = p, v2 = v;
                Step(ref p2, ref v2, dt, k, g);
                Vector3 seg = p2 - p;
                float len = seg.magnitude;
                if (len < 1e-4f) break;
                Vector3 dir = seg / len;
                if (Segment(p, dir, len, mask, a, ignore, side, vMax, s, v.magnitude, v2.magnitude, out float f, out PenResult pr, out float pen, out bool armed))
                {
                    res.Hit = true;
                    res.Point = pr.Point;
                    res.Dir = dir;
                    res.Speed = Mathf.Lerp(v.magnitude, v2.magnitude, f);
                    res.Time = t + dt * f;
                    res.Path = s + len * f;
                    res.Pen = pen;
                    res.Armed = armed;
                    res.Probe = pr;
                    res.Unit = pr.Unit;
                    Add(pr.Point, res.Time, res.Path, res.Speed);
                    LastSteps = step + 1;
                    break;
                }
                p = p2; v = v2; t += dt; s += len;
                Add(p, t, s, v.magnitude);
                if (s > maxPath || p.y < -2000f) break;
            }
            if (LastSteps == 0) LastSteps = _n;
            // moving vehicles, each in its own frame, before whatever the static march ran into
            if (Moving.Count > 0 && MovingHit(a, ignore, side, res.Hit ? res.Time : float.MaxValue, out ArcResult mv))
                res = mv;
            return res.Hit;
        }

        static void Add(Vector3 p, float t, float s, float sp)
        {
            if (_n >= _p.Length) return;
            _p[_n] = p; _t[_n] = t; _s[_n] = s; _sp[_n] = sp; _n++;
        }

        static bool IsMoving(Collider col)
        {
            if (Moving.Count == 0 || col == null) return false;
            var u = CodeUtils.GetParentUnitFollowAware(col.transform);
            return u != null && Moving.Contains(u);
        }

        /// arc walked in each moving vehicle's frame: q(t) = p(t) - v * t. first one it meets (before `before`)
        static bool MovingHit(AmmoType a, Unit ignore, Faction side, float before, out ArcResult best)
        {
            best = default;
            float bestT = before;
            int mask = PenProbe.Mask;
            for (int m = 0; m < Moving.Count; m++)
            {
                var u = Moving[m];
                if (u == null || u == ignore) continue;
                Vector3 vel = MovingVel[m], c = u.transform.position + u.transform.up * 1.4f;
                for (int k = 0; k + 1 < _n; k++)
                {
                    if (_t[k] > bestT) break;
                    Vector3 q0 = _p[k] - vel * _t[k], q1 = _p[k + 1] - vel * _t[k + 1];
                    Vector3 seg = q1 - q0;
                    float len = seg.magnitude;
                    if (len < 1e-4f) continue;
                    Vector3 dir = seg / len;
                    // only the bit of the relative path that passes the vehicle
                    float along = Mathf.Clamp(Vector3.Dot(c - q0, dir), 0f, len);
                    if ((q0 + dir * along - c).sqrMagnitude > 12f * 12f) continue;
                    Vector3 o = q0;
                    float left = len;
                    for (int i = 0; i < 8 && left > 0.001f; i++)
                    {
                        LastRaycasts++;
                        if (!RaycastColliderUtils.Raycast(new Ray(o, dir), out RaycastHit h, left, mask)) break;
                        float adv = h.distance + 0.01f;
                        o += dir * adv; left -= adv;
                        if (h.collider == null || h.collider.isTrigger || !PenProbe.BelongsTo(h.collider, u)) continue;
                        float f = Mathf.Clamp01((len - left) / len);
                        float tt = Mathf.Lerp(_t[k], _t[k + 1], f);
                        if (tt >= bestT) break;
                        float sp = Mathf.Lerp(_sp[k], _sp[k + 1], f), path = Mathf.Lerp(_s[k], _s[k + 1], f);
                        float pen = PenAt(a, sp, _vMax, path, out bool armed);
                        if (!PenProbe.Probe(h.point - dir * 0.3f, dir, 3f, a, ignore, side, out PenResult pr, dir, -1f, 0f, pen) || pr.Unit != u) pr.Unit = u;
                        best = new ArcResult { Hit = true, Point = pr.Hit ? pr.Point : h.point, Dir = dir, Speed = sp, Time = tt, Path = path, Pen = pen, Armed = armed, Probe = pr, Unit = u, Moving = true };
                        bestT = tt;
                        break;
                    }
                }
            }
            return best.Hit;
        }

        /// flight along a straight aim line: time to reach `range` m down it and how far the round dropped below the line
        /// by then (game's RK4 + drag), for a weapon's lead
        public static bool Flight(AmmoType a, Vector3 dir, Vector3 ownVel, float range, out float time, out float drop)
        {
            time = 0f; drop = 0f;
            if (a == null || range <= 0f) return false;
            dir.Normalize();
            Vector3 v0 = a.MuzzleVelocity * dir + ownVel;
            float temp = 1f;
            try { temp = 1f + 0.0004f * (GHPC.World.WorldEnvironmentManager.AmmoTempFahrenheit - 70f); } catch { }
            Vector3 p = Vector3.zero, v = v0 * temp;
            float k = PenProbe.DragK(a);
            Vector3 g = a.Guidance != 0 ? Vector3.zero : Physics.gravity;
            float dt = Mathf.Clamp(25f / Mathf.Max(v0.magnitude, 50f), 0.004f, 0.05f);
            for (int i = 0; i < 2000; i++)
            {
                Vector3 p0 = p;
                Step(ref p, ref v, dt, k, g);
                float a0 = Vector3.Dot(p0, dir), a1 = Vector3.Dot(p, dir);
                if (a1 >= range)
                {
                    float f = Mathf.InverseLerp(a0, a1, range);
                    Vector3 at = Vector3.Lerp(p0, p, f);
                    time = (i + f) * dt;
                    drop = Vector3.Dot(dir * range - at, Vector3.up);
                    return true;
                }
                if (v.sqrMagnitude < 25f) return false;
            }
            return false;
        }

        /// BallisticEvaluatorRK4.Integrate, step for step
        static void Step(ref Vector3 p, ref Vector3 v, float dt, float k, Vector3 g)
        {
            Vector3 a1 = Acc(v, k, g);
            Vector3 v2 = v + a1 * dt, a2 = Acc(v2, k, g);
            Vector3 v3 = v + a2 * (0.5f * dt), a3 = Acc(v3, k, g);
            Vector3 v4 = v + a3 * (0.5f * dt), a4 = Acc(v4, k, g);
            Vector3 v5 = v + a4 * dt, a5 = Acc(v5, k, g);
            Vector3 dx = (v2 + 2f * v3 + 2f * v4 + v5) * (1f / 6f);
            Vector3 dv = (a2 + 2f * a3 + 2f * a4 + a5) * (1f / 6f);
            p += dx * dt;
            v += dv * dt;
        }

        static Vector3 Acc(Vector3 v, float k, Vector3 g) => g - k * v.magnitude * v;

        /// round's pen after `path` m at speed `speed` (LiveRound.CurrentPenRating)
        public static float PenAt(AmmoType a, float speed, float vMax, float path, out bool armed)
        {
            armed = a.Category != AmmoType.AmmoCategory.ShapedCharge || a.ArmingDistance <= 0f || path >= a.ArmingDistance;
            float pen = a.RhaPenetration;
            if (a.Category == AmmoType.AmmoCategory.ShapedCharge) return armed ? pen : a.MaxSpallRha;
            if (a.Guidance != 0) return pen;
            float r = speed / Mathf.Max(vMax, 1f);
            float m = r * r;
            if (a.NutationPenaltyDistance > 0f && path < a.NutationPenaltyDistance)
                m *= 1f - 0.07f * (a.NutationPenaltyDistance - path) / a.NutationPenaltyDistance;
            return pen * m;
        }

        static bool Segment(Vector3 o, Vector3 dir, float len, int mask, AmmoType a, Unit ignore, Faction side, float vMax, float s0, float va, float vb,
            out float frac, out PenResult pr, out float pen, out bool armed)
        {
            frac = 0f; pr = default; pen = 0f; armed = true;
            float done = 0f;
            int terrain = CodeUtils.LAYER_INDEX_TERRAIN, groups = CodeUtils.LAYER_INDEX_RAYCAST_ONLY_GROUPS;
            for (int i = 0; i < 10; i++)
            {
                float left = len - done;
                if (left <= 0.001f) return false;
                LastRaycasts++;
                if (!Physics.Raycast(o + dir * done, dir, out RaycastHit h, left, mask, QueryTriggerInteraction.Collide)) return false;
                float at = done + h.distance;
                done = at + 0.01f;
                var col = h.collider;
                if (col == null) continue;
                var go = col.gameObject;
                if (ignore != null && PenProbe.BelongsTo(col, ignore)) continue;
                if (go.layer == terrain && Vector3.Angle(Vector3.up, h.normal) > 85f) continue;   // tree trunks: rounds pass
                if (IsMoving(col)) continue;                                                       // worked out in its own frame
                if (col.isTrigger && go.layer != groups && !go.CompareTag("Compartment")) continue;
                // something the round meets: read it like a round would, from just in front
                float f = at / len;
                float sp = Mathf.Lerp(va, vb, f);
                pen = PenAt(a, sp, vMax, s0 + at, out armed);
                LastRaycasts++;
                if (PenProbe.Probe(h.point - dir * 0.3f, dir, (len - at) + 1.3f, a, ignore, side, out pr, dir, -1f, 0f, pen))
                {
                    frac = Mathf.Clamp01((at + Mathf.Max(pr.Distance - 0.3f, 0f)) / len);
                    return true;
                }
                // vehicle bounding box with nothing solid on this line: keep going through it
            }
            return false;
        }

        /// where the last arc was after flying `path` m (clamped to what was computed)
        public static bool PointAtPath(float path, out Vector3 p, out float time)
        {
            p = default; time = 0f;
            if (_n < 2) return false;
            for (int i = 1; i < _n; i++)
                if (_s[i] >= path)
                {
                    float f = Mathf.InverseLerp(_s[i - 1], _s[i], path);
                    p = Vector3.Lerp(_p[i - 1], _p[i], f);
                    time = Mathf.Lerp(_t[i - 1], _t[i], f);
                    return true;
                }
            p = _p[_n - 1]; time = _t[_n - 1];
            return false;
        }

        /// where the last arc first crossed a sphere of `range` m around `from`, and when
        public static bool PointAtRange(Vector3 from, float range, out Vector3 p, out float time)
        {
            p = default; time = 0f;
            if (_n < 2) return false;
            float r2 = range * range;
            for (int i = 1; i < _n; i++)
            {
                float d1 = (_p[i] - from).sqrMagnitude;
                if (d1 < r2) continue;
                float d0 = Mathf.Sqrt((_p[i - 1] - from).sqrMagnitude), d1s = Mathf.Sqrt(d1);
                float f = d1s > d0 ? Mathf.Clamp01((range - d0) / (d1s - d0)) : 1f;
                p = Vector3.Lerp(_p[i - 1], _p[i], f);
                time = Mathf.Lerp(_t[i - 1], _t[i], f);
                return true;
            }
            return false;
        }
    }
}
