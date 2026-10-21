using System;
using System.Collections.Generic;
using System.Linq;
using GHPC;
using GHPC.Effects;
using UnityEngine;

namespace GHPCHitCam
{
    /// Flame forced out of the holes in a burning vehicle, in the world. The same hull pressure as the x-ray
    /// (HullPressure), fed live by the game's own events on the game's clock: charges catching fire, hatches blown open,
    /// flame bursts, the turret thrown off, detonations. At every hole the rounds made into the compartment (the game's own
    /// impact exits) a copy of the game's own small-hole flame points out along the hole, stretched along its axis as far
    /// as the pressure drives it (~12 x d sqrt(p / p0)); the game's flame comes in fixed sizes, so the copy's particles
    /// are simulated in its own (scaled) frame. The flame already out burns on ~0.25 s after the push drops. The game's
    /// own exits and fires are left as they are (its own flame at the hole stays, the copy grows out of the same spot).
    /// Setting: WorldVentJets.
    internal class WorldVents : MonoBehaviour
    {
        static readonly List<WorldVents> _all = new List<WorldVents>();
        const int MaxVehicles = 4, MaxJets = 6;
        class Jet { public Vector3 L, Dir; public float D, Max, Push; public GameObject Go; public Transform Fx, FxBody; public Vector3 Root; public IFireParticleSystem Fire; public bool On; public float OnSince = -1f; public int Snaps; }

        /// How long the game's small-hole flame looks unstretched (its own fixed size: a ~0.35 m cone).
        const float FlameLooks = 0.35f;

        Unit _unit;
        HullPressure _hp;
        readonly List<(HitEvent e, HitRecord rec)> _breaches = new List<(HitEvent, HitRecord)>();
        readonly HashSet<HitEvent> _seen = new HashSet<HitEvent>();
        readonly List<Jet> _jets = new List<Jet>();
        float _born, _log = -1f, _peak = 1f, _detAt = -1f;

        /// Every event the recorder adds (HitRecord.AddEvent): the live ones that matter to the hull's pressure.
        public static void OnEvent(HitRecord rec, HitEvent e)
        {
            try
            {
                if (rec == null || e == null || rec.Target == null || !e.HasPos) return;
                if (!Settings.WorldDamage.Value || !Settings.WorldVentJets.Value) return;
                if (Mathf.Abs(e.RealT - Time.time) > 1f) return;          // live only (not a replay or an archive)
                switch (e.Kind)
                {
                    case EvKind.Breach: For(rec)?._breaches.Add((e, rec)); break;
                    case EvKind.Fire:
                    case EvKind.AmmoFire:
                        if (e.Kind == EvKind.AmmoFire || Timeline.Propellant(e)) For(rec)?._hp.AddCharge(e.L, Time.time, e.Extent, e.Kind == EvKind.AmmoFire);
                        break;
                    case EvKind.VentFire: For(rec)?.Opening(e, false); break;
                    case EvKind.HatchBlown: For(rec)?.Opening(e, true); break;
                    case EvKind.FireBurst:
                        {
                            var v = For(rec);
                            v?.Opening(e, true);
                            v?.Detonation(e, e.After);       // a burst's ratio is the detonation's TNT
                            break;
                        }
                    case EvKind.TurretOff: For(rec)?._hp.AddOpening(e.L, 2.5f, Time.time); break;     // the turret ring open to the sky
                    case EvKind.Explosion:
                        if (!Timeline.OwnWarhead(rec, e)) For(rec)?.Detonation(e, e.After);
                        break;
                }
            }
            catch (Exception ex) { HitCamMod.Log.Warning("World vents: " + ex.Message); }
        }

        static WorldVents For(HitRecord rec)
        {
            var u = rec.Target;
            _all.RemoveAll(v => v == null);
            foreach (var v in _all) if (v._unit == u) return v;
            if (rec.Snapshot == null) return null;
            // the oldest one with nothing burning makes room
            while (_all.Count >= MaxVehicles)
            {
                var old = _all.OrderBy(v => v._jets.Any(j => j.On) ? 1 : 0).ThenBy(v => v._born).First();
                _all.Remove(old);
                Destroy(old);
            }
            var w = u.gameObject.AddComponent<WorldVents>();
            w._unit = u;
            w._born = Time.time;
            w._hp = new HullPressure(HullPressure.FreeVolume(HullPressure.InnerOf(rec.Snapshot)));
            _all.Add(w);
            return w;
        }

        void Opening(HitEvent e, bool open)
        {
            if (e.Subject == "the hole") return;          // a hole the round made: counted with the holes
            float r = e.Extent.x > 0.005f ? e.Extent.x : e.Power > 0.005f ? e.Power : 0.2f;
            _hp.AddOpening(e.L, Mathf.PI * r * r * (open ? 1f : 0.1f), Time.time);
        }

        void Detonation(HitEvent e, float tnt)
        {
            if (!_hp.Detonation(e.RealT, tnt, Time.time)) return;
            _detAt = Time.time;
            if (AutoTest.Active) HitCamMod.Log.Msg($"World vents {Recorder.SafeName(_unit)}: detonation {tnt:0.0} kg TNT -> {_hp.Ratio:0.0} bar in {_hp.V:0.0} m^3 of free air, {_jets.Count} holes");
        }

        /// The holes near the burning charges (the same compartment, roughly), each with the game's fire effect on it.
        void FindJets()
        {
            if (_jets.Count >= MaxJets) return;
            var pem = ParticleEffectsManager.Instance;
            if (pem == null) return;
            foreach (var (e, rec) in _breaches)
            {
                if (_seen.Contains(e) || _jets.Count >= MaxJets) continue;
                bool near = false;
                foreach (var c in _hp.Charges) if ((c.l - e.L).sqrMagnitude < 3.5f * 3.5f) { near = true; break; }
                if (!near) continue;
                _seen.Add(e);
                HullPressure.VentShape(e, rec.ArmorHits, out Vector3 dirL, out float r, out ArmorHit plate, out float los);
                var t = _unit.transform;
                // on the game's own exit marker: the hole into the compartment itself (behind a skirt it is the hull's,
                // not the skirt's), riding with the hull
                var marker = (e.Ref as CompartmentExit)?.PositionMarker;
                Transform host = marker != null ? marker : t;
                Vector3 at = marker != null ? marker.position : t.TransformPoint(e.L), dir = t.TransformDirection(dirL).normalized;
                float max = HullPressure.MaxReach(2f * r);
                // something of the vehicle in its way outside (a skirt, a stowage box): it splashes there
                float best = float.MaxValue;
                foreach (var c in WorldDamage.HullColliders(_unit).set)
                    if (c != null && c.Raycast(new Ray(at + dir * 0.1f, dir), out var h, max) && h.distance > 0.15f) best = Mathf.Min(best, h.distance);
                if (best < float.MaxValue) max = Mathf.Min(max, best + 0.35f);
                max = Mathf.Max(max, 0.4f);
                int layer = WorldDamage.VisibleLayer(_unit);
                var go = new GameObject("HitCam vent jet") { layer = layer };
                go.transform.SetParent(host, false);
                go.transform.position = at + dir * 0.03f;      // just out of the hole
                var j = new Jet { L = e.L, Dir = dirL, D = 2f * r, Max = max, Go = go };
                j.Fire = Spawn(pem, go.transform, dir, out j.Fx);
                if (j.Fx != null && j.Fx.childCount > 0) j.FxBody = j.Fx.GetChild(0);
                _jets.Add(j);
                _hp.AddHole(2f * r);
                HitCamMod.Log.Msg($"World vents {Recorder.SafeName(_unit)}: hole into {e.Subject} at {e.L} ({2000f * r:0} mm across{(plate != null ? $" in {plate.Name}, {los * 1000f:0} mm" : "")}), " +
                                  $"flame out along {dirL} up to {max:0.00} m{(j.Fire == null ? " (the game has no small-hole flame to use)" : "")}");
            }
        }

        /// A copy of the game's own small-hole flame (the one it shows at a round's hole), its flame axis out along the hole,
        /// under a frame of its own that stretches it (fx); its particles simulated in that frame and scaled with it.
        static IFireParticleSystem Spawn(ParticleEffectsManager pem, Transform at, Vector3 dir, out Transform fx)
        {
            fx = null;
            foreach (var type in new[] { FlammableSourceType.SmallHole, FlammableSourceType.Surface })
            {
                var prefab = pem.GetFirePrefab(type);
                if (prefab == null) continue;
                var frame = new GameObject("HitCam vent flame frame").transform;
                frame.SetParent(at, false);
                frame.rotation = Quaternion.FromToRotation(Vector3.up, dir);      // its Y is the flame axis
                var go = Instantiate(prefab, frame);
                go.name = "HitCam vent flame (" + type + ")";
                var fps = go.GetComponentInChildren<IFireParticleSystem>();
                if (fps == null) { Destroy(frame.gameObject); continue; }
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = fps.Orientation == FireParticleSystemOrientation.YOut ? Quaternion.identity : Quaternion.FromToRotation(Vector3.forward, Vector3.up);
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                    main.simulationSpace = ParticleSystemSimulationSpace.Local;
                }
                foreach (var tr in frame.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = at.gameObject.layer;
                fps.Stop();
                fx = frame;
                return fps;
            }
            return null;
        }

        void Update() { using (Perf.T("WorldVents.Update")) TimedUpdate(); }
        void TimedUpdate()
        {
            if (_unit == null) { Destroy(this); return; }
            if (!Settings.WorldVentJets.Value || !Settings.WorldDamage.Value) { StopAll(); return; }
            if (!_hp.Started) return;
            float now = Time.time, dt = now - _hp.Clock;
            if (_hp.Charges.Count > 0) FindJets();
            _hp.StepTo(now);
            if (dt <= 0f) return;
            float pr = _hp.Ratio;
            _peak = Mathf.Max(_peak, pr);
            int on = 0;
            foreach (var j in _jets)
            {
                if (j.Go == null) continue;        // its hole went with the part it was in
                float reach = HullPressure.Reach(j.D, pr);
                // the burning gas already out of the hole goes on flaming for a moment after the push drops
                j.Push = Mathf.Max(Mathf.Clamp01(reach / j.Max), j.Push * Mathf.Exp(-dt / 0.25f));
                float len = j.Push * j.Max;
                bool want = len > 0.05f;
                try
                {
                    if (j.Fire != null && j.Fx != null)
                    {
                        if (want)
                        {
                            // longer along the flame, a little wider with the push (the jet spreads to ~d sqrt(p / p0))
                            float along = Mathf.Max(len / FlameLooks, 0.2f);
                            float across = Mathf.Clamp(Mathf.Sqrt(along) * Mathf.Sqrt(j.D / 0.06f), 0.5f, 2.5f);
                            if (!j.On)
                            {
                                j.Fire.SetAllRatios(1f);
                                j.Fire.Play();
                                j.Root = FlameRoot(j);
                            }
                            j.Fx.localScale = new Vector3(across, along, across);
                            // stretched about where its flame starts (the emitter of the size that plays sits up its axis)
                            if (j.FxBody != null) j.FxBody.localPosition = Vector3.Scale(j.Root, new Vector3(1f / across - 1f, 1f / along - 1f, 1f / across - 1f));
                        }
                        else if (j.On) j.Fire.Stop();
                    }
                }
                catch { j.Fire = null; }
                if (want && !j.On) j.OnSince = now;
                j.On = want;
                if (want) on++;
                // self-test: close-ups of the hole once the hit's own smoke thins, later on, and right at a detonation
                bool snap = j.Snaps == 0 ? now - j.OnSince > 1.5f : j.Snaps == 1 ? now - j.OnSince > 3f : _detAt > 0f && now - _detAt > 0.05f && now - _detAt < 0.5f && j.Snaps < 3;
                if (AutoTest.Opt("capture", true) && want && snap && now - _snapAt > 0.3f)
                {
                    if (j.Snaps == 2) _detAt = -1f;
                    _snapAt = now;
                    var t = _unit.transform;
                    Vector3 at = j.Go.transform.position, dir = j.Fx != null ? j.Fx.up : t.TransformDirection(j.Dir);
                    Vector3 side = Vector3.Cross(Vector3.up, dir).normalized;
                    if (side.sqrMagnitude < 0.5f) side = t.right;
                    Vector3 from = at + dir * 2.2f + side * 3.2f + Vector3.up * 1.1f;
                    ModuleTest.Snap(at + dir * Mathf.Min(len, 1.5f) * 0.5f, from, $"vent{_snapN++}_{Recorder.SafeName(_unit).Replace(' ', '_')}", $"{len:0.00} m flame at {pr:0.00} bar");
                    j.Snaps++;
                }
            }
            if (AutoTest.Active && now - _log >= 0.25f && pr > 1.02f)
            {
                _log = now;
                HitCamMod.Log.Msg($"World vents {Recorder.SafeName(_unit)}: {pr:0.00} bar (gas in {_hp.Gas:0.00} kg/s, open {_hp.Area * 1e4f:0} cm^2 of it {_hp.HoleArea * 1e4f:0} holes), " +
                                  $"{on}/{_jets.Count} holes flaming" + string.Concat(_jets.Select(j => $"; {j.Push * j.Max:0.00} m")));
            }
            // all burnt out: gone
            if (on == 0 && pr < 1.02f && _hp.Charges.Count > 0 && now - _hp.Charges.Max(c => c.t) > 45f) Destroy(this);
        }

        /// Where the flame that is playing starts, in its frame unstretched: its emitter and the offset of its emission shape.
        static Vector3 FlameRoot(Jet j)
        {
            if (j.Fx == null || j.FxBody == null) return Vector3.zero;
            Vector3 scale = j.Fx.localScale, body = j.FxBody.localPosition;
            j.Fx.localScale = Vector3.one;
            j.FxBody.localPosition = Vector3.zero;
            Vector3 root = Vector3.zero;
            foreach (var ps in j.FxBody.GetComponentsInChildren<ParticleSystem>(false))
            {
                var sh = ps.shape;
                root = j.Fx.InverseTransformPoint(ps.transform.TransformPoint(sh.enabled ? sh.position : Vector3.zero));
                break;
            }
            j.Fx.localScale = scale;
            j.FxBody.localPosition = body;
            if (AutoTest.Active) HitCamMod.Log.Msg($"World vents: flame root {root:F2} in its frame");
            return root;
        }

        static float _snapAt = -9f;
        static int _snapN;

        void StopAll()
        {
            foreach (var j in _jets)
            {
                try { if (j.On) j.Fire?.Stop(); } catch { }
                j.On = false;
                j.Push = 0f;
            }
        }

        void OnDestroy()
        {
            foreach (var j in _jets)
            {
                try { j.Fire?.Stop(); } catch { }
                if (j.Go != null) Destroy(j.Go, 3f);      // the last flames die out on their own
            }
            _all.Remove(this);
        }
    }
}
