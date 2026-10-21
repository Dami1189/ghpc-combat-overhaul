using System.Collections.Generic;
using UnityEngine;

namespace GHPCHitCam
{
    /// The pressure in a vehicle's hull while propellant burns in it, the same for the x-ray and the world (on any clock:
    /// the x-ray's playback time or the game's). Gas from the burning charges comes in and leaves through every opening:
    /// the leaks round the hatches and the gun, the holes the rounds made, the exits the game opened. Above ~1.8 x the
    /// outside pressure the flow through an opening is choked (sonic), below it subsonic, and the hot gas loses its heat to
    /// the steel round it. A detonation adds its quasi-static pressure. The pressure drives flame out of the holes.
    internal class HullPressure
    {
        public const float Atm = 101325f;
        const float GasRT = 330f * 2400f;    // propellant gas: R ~330 J/kg K at ~2400 K
        const float Gamma = 1.25f;
        const float Cd = 0.8f;               // discharge coefficient of a ragged hole
        const float Leak = 0.004f;           // m^2: gaps round the hatches, the gun, vision blocks, vents
        const float CoolTau = 1f;            // s: the hot gas losing its heat to the steel round it
        const float OpenTime = 0.06f;        // s: a hatch blown open / the turret lifting clears its opening over tens of ms

        public readonly float V;             // the free air inside, m^3
        public float P { get; private set; } = Atm;
        public float Ratio => P / Atm;
        public float Clock { get; private set; } = float.NaN;
        public bool Started => !float.IsNaN(Clock);
        public float Gas { get; private set; }        // kg/s coming in at the last step
        public float Area { get; private set; }       // m^2 open at the last step
        public float HoleArea { get; private set; }   // m^2 of the holes the rounds made
        public readonly List<(Vector3 l, float t, float kg)> Charges = new List<(Vector3, float, float)>();
        readonly List<(Vector3 l, float area, float t0)> _exits = new List<(Vector3, float, float)>();
        float _boomT = float.MaxValue, _spikeRealT = float.NaN, _spikeKg;

        public HullPressure(float freeVolume) { V = freeVolume; }

        /// The inner box the modules and crew take up (as the x-ray's walls): not the tracks, nothing bigger than 4 m,
        /// 0.25 m round them.
        public static Bounds InnerOf(GhostSnapshot s)
        {
            bool first = true;
            var inner = new Bounds();
            void Add(Bounds b) { if (first) { inner = b; first = false; } else inner.Encapsulate(b); }
            foreach (var mp in s.Modules)
                foreach (var sh in mp.Shapes)
                {
                    var b = sh.LocalAabb();
                    if (mp.Cat != ModCat.Tracks && b.size.magnitude <= 4f) Add(b);
                }
            foreach (var c in s.Crew) foreach (var sh in c.Shapes) Add(sh.LocalAabb());
            if (first) inner = s.LocalBounds;
            inner.Expand(0.25f);
            return inner;
        }

        /// The air inside: that box less what fills it (ammunition, engine, crew, equipment).
        public static float FreeVolume(Bounds inner) => Mathf.Clamp(inner.size.x * inner.size.y * inner.size.z * 0.35f, 2.5f, 25f);

        /// A charge (or the rack) burning: gas from about its mass over a few seconds. The mass is read off the size of the
        /// burning thing (~580 kg per m^3 of its box: grains loaded in a case), 5 kg if unknown. False if already burning.
        public bool AddCharge(Vector3 l, float t, Vector3 extent, bool rack)
        {
            foreach (var c in Charges) if ((c.l - l).sqrMagnitude < 0.04f) return false;
            float kg = extent.x > 0.01f && extent.y > 0.01f && extent.z > 0.01f ? 8f * extent.x * extent.y * extent.z * 580f : 5f;
            Charges.Add((l, t, Mathf.Clamp(kg, 1.5f, 12f) * (rack ? 1.5f : 1f)));
            if (!Started) Clock = t;
            return true;
        }

        /// An opening the game made, of this area, clearing from time t (a bigger one at the same place replaces it).
        public void AddOpening(Vector3 l, float area, float t)
        {
            for (int i = 0; i < _exits.Count; i++)
                if ((_exits[i].l - l).sqrMagnitude < 0.04f)
                {
                    if (area > _exits[i].area) _exits[i] = (l, area, t);
                    return;
                }
            _exits.Add((l, area, t));
        }

        /// A hole a round made, of this diameter.
        public void AddHole(float d) => HoleArea += Mathf.PI * 0.25f * d * d;

        /// What the game opened is open by time t (each clearing over OpenTime).
        public float OpenArea(float t)
        {
            float a = 0f;
            foreach (var x in _exits) a += x.area * Mathf.Clamp01((t - x.t0) / OpenTime);
            return a;
        }

        /// A detonation's pressure, once per detonation (its several reports, within 0.3 s, are the same one), at its full
        /// TNT: its energy suddenly in the hull's air, quasi-static pressure ~(gamma-1) E / V. True if it raised the pressure.
        public bool Detonation(float realT, float tnt, float t)
        {
            if (tnt <= 0f) return false;
            if (!float.IsNaN(_spikeRealT) && Mathf.Abs(realT - _spikeRealT) < 0.3f)
            {
                if (tnt <= _spikeKg) return false;
                Spike(tnt - _spikeKg, t);
                _spikeKg = tnt;
                return true;
            }
            _spikeRealT = realT;
            _spikeKg = tnt;
            Spike(tnt, t);
            return true;
        }

        /// Another detonation, separate from any before (the rest of the rack going a moment later).
        public void Spike(float tnt, float t)
        {
            if (!Started) Clock = t;
            P = Mathf.Min(P + (Gamma - 1f) * tnt * 4.184e6f / V, 5e6f);
            _boomT = Mathf.Min(_boomT, t);
        }

        /// Gas from the burning charges at time t (kg/s): each burns hard for ~2.5 s, then down; less is left after a
        /// detonation.
        public float GasRate(float t)
        {
            float g = 0f;
            foreach (var c in Charges)
            {
                float age = t - c.t;
                if (age < 0f) continue;
                float shape = Mathf.Clamp01(age / 0.15f) * (age < 2.5f ? 1f : Mathf.Max(0.2f, Mathf.Exp(-(age - 2.5f) / 2.5f)));
                g += c.kg / 4f * shape;
            }
            return t > _boomT ? g * 0.5f : g;
        }

        /// On to time t, in steps of 4 ms at most.
        public void StepTo(float t)
        {
            if (!Started) return;
            float dt = t - Clock;
            if (dt <= 0f) return;
            int n = Mathf.Clamp(Mathf.CeilToInt(dt / 0.004f), 1, 250);
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                float ti = Clock + h * (i + 1);
                Gas = GasRate(ti);
                Area = Leak + HoleArea + OpenArea(ti);
                float dp = GasRT / V * (Gas - Outflow(P, Area)) - (P - Atm) / CoolTau;
                P = Mathf.Max(Atm, P + dp * h);
            }
            Clock = t;
        }

        /// Gas out through an opening (kg/s): choked (sonic) above ~1.8 x outside pressure, else the subsonic flow.
        static float Outflow(float p, float area)
        {
            if (p <= Atm) return 0f;
            float r = Atm / p;
            const float crit = 0.5549f;   // (2 / (gamma + 1))^(gamma / (gamma - 1)) for gamma 1.25
            if (r <= crit) return Cd * area * p * 7.40e-4f;   // sqrt(gamma / RT) (2 / (gamma + 1))^((gamma + 1) / 2 (gamma - 1))
            float k = Mathf.Pow(r, 2f / Gamma) - Mathf.Pow(r, (Gamma + 1f) / Gamma);
            return Cd * area * p * Mathf.Sqrt(2f * Gamma / ((Gamma - 1f) * GasRT) * Mathf.Max(k, 0f));
        }

        /// How far the flame reaches out of a hole of diameter d at this pressure ratio: an under-expanded jet spreads right
        /// outside to about d sqrt(p / p_outside), and its fuel-rich gas burns on with the air it pulls in for about a dozen
        /// of those.
        public static float Reach(float d, float pr)
        {
            if (pr <= 1.02f) return 0f;
            float k = pr >= 1.8f ? 1f : (pr - 1f) / 0.8f;
            return 12f * d * Mathf.Sqrt(pr) * k;
        }

        /// The longest it can be (at ~40 bar, at most 3.2 m).
        public static float MaxReach(float d) => Mathf.Min(12f * d * Mathf.Sqrt(40f), 3.2f);

        /// A hole the game opened into a compartment (a Breach event, target-local): how big it is (as the hole drawn in
        /// that plate: the round's own hit on it, the thickest plate there) and which way the gas leaves it (along the
        /// plate's normal, or along the round's channel through thick armour).
        public static void VentShape(HitEvent e, IEnumerable<ArmorHit> hits, out Vector3 dir, out float r, out ArmorHit plate, out float los)
        {
            Vector3 outL = e.HasDir && e.DirL.sqrMagnitude > 1e-6f ? e.DirL.normalized : Vector3.up;   // back out along the round's path
            plate = null;
            foreach (var a in hits)
                if (a.Penetrated && !a.IsSpall && (a.L - e.L).sqrMagnitude < 0.15f * 0.15f && (plate == null || a.Los > plate.Los)) plate = a;
            float gameR = e.Extent.x > 0.002f ? e.Extent.x : e.Power > 0.002f ? e.Power : 0.015f;
            r = Mathf.Clamp(plate != null && plate.HoleRadius > 0.003f ? plate.HoleRadius : gameR * 1.5f, 0.008f, 0.15f);
            dir = outL;
            los = 0f;
            if (plate == null) return;
            Vector3 n = plate.NormalL.sqrMagnitude > 1e-6f ? plate.NormalL.normalized : outL;
            if (Vector3.Dot(n, outL) < 0f) n = -n;
            los = Mathf.Max(plate.Los, plate.Nominal) * 0.001f;
            float w = Mathf.Clamp01((los / (2f * r) - 0.5f) / 2f);
            dir = Vector3.Slerp(n, outL, w).normalized;
        }
    }
}
