using System;
using System.Collections.Generic;
using System.Linq;
using GHPC;
using GHPC.Effects;
using GHPC.Weapons;
using UnityEngine;
using DC = GHPC.Equipment.DestructibleComponent;

namespace GHPCHitCam
{
    /// dead engine burning out through its deck (or a heli's cowling): the hit cuts fuel, oil and hydraulic lines over an
    /// exhaust side way above the self-ignition point of diesel/oil (~210-300 C), so the spray catches. a shaped charge jet
    /// or HE burst lights more of it than a solid shot; grows for a few s, burns while the spilled fuel lasts, then smoulders
    /// uses the game's own fire/smoke fx at the outside surface above the engine's real hit zone (not its pivot)
    /// game's own fires (flammables, crew compartments) are left alone
    internal class EngineFire : MonoBehaviour
    {
        static readonly List<EngineFire> _all = new List<EngineFire>();
        const int MaxFires = 6;

        IFireParticleSystem _fire, _smoke;
        float _t0, _peak, _burn, _smokeFor;
        bool _ending;

        public static void Start(Unit u, DC dc, AmmoType ammo)
        {
            var pem = ParticleEffectsManager.Instance;
            if (pem == null || u == null) return;
            var col = dc.ComponentCollision;
            Bounds b = col != null ? WorldModules.ZoneBounds(col) : new Bounds(dc.transform.position, Vector3.one);
            var urb = u.GetComponent<Rigidbody>();
            Transform hull = urb != null ? urb.transform : u.transform;
            Vector3 up = hull.up;
            // outside above the engine: cast down onto the vehicle's own armour from above
            Vector3 from = b.center + up * (b.extents.magnitude + 3f);
            Vector3 at = b.center + up * b.extents.y;
            float best = float.MaxValue;
            foreach (var c in WorldDamage.HullColliders(u).set)
            {
                if (c == null || !c.bounds.IntersectRay(new Ray(from, -up))) continue;
                if (c.Raycast(new Ray(from, -up), out var h, 8f) && h.distance < best) { best = h.distance; at = h.point; }
            }
            bool air = u.Type == UnitType.AirVehicle;
            bool jet = ammo != null && (ammo.Category == AmmoType.AmmoCategory.ShapedCharge || ammo.Category == AmmoType.AmmoCategory.Explosive);
            float luck = PartKit.Hash(u.GetInstanceID(), dc.GetInstanceID());
            float size = (jet ? 1.0f : ammo != null ? 0.7f : 0.55f) * (0.8f + 0.4f * luck);
            var go = new GameObject("HitCam engine fire") { layer = WorldDamage.VisibleLayer(u) };
            go.transform.SetParent(hull, false);
            go.transform.position = at + up * 0.02f;
            go.transform.rotation = Quaternion.LookRotation(up, hull.forward);
            var ef = go.AddComponent<EngineFire>();
            ef._t0 = Time.time;
            ef._peak = size;
            ef._burn = (air ? 40f : 55f) + 60f * luck * (jet ? 1.3f : 1f);
            ef._smokeFor = 90f;
            ef._fire = Spawn(pem, go.transform, up, true);
            ef._smoke = Spawn(pem, go.transform, up, false);
            if (ef._fire == null && ef._smoke == null) { Destroy(go); HitCamMod.Log.Msg("World engine fire: the game has no fire effects to use"); return; }
            _all.RemoveAll(f => f == null);
            _all.Add(ef);
            while (_all.Count > MaxFires) { if (_all[0] != null) Destroy(_all[0].gameObject); _all.RemoveAt(0); }
            HitCamMod.Log.Msg($"World engine fire {Recorder.SafeName(u)} '{dc.Name}': {(jet ? "jet / blast" : ammo != null ? "shot" : "fragments")} into the engine -> " +
                              $"fire {size:0.00} for {ef._burn:0} s at the {(best < float.MaxValue ? "deck" : "zone's top")} {hull.InverseTransformPoint(at)}");
        }

        static IFireParticleSystem Spawn(ParticleEffectsManager pem, Transform at, Vector3 up, bool fire)
        {
            var order = fire
                ? new[] { FlammableSourceType.Surface, FlammableSourceType.TopLarge, FlammableSourceType.SmallHatch, FlammableSourceType.CrewHatch, FlammableSourceType.SmallHole }
                : new[] { FlammableSourceType.TopLarge, FlammableSourceType.Surface, FlammableSourceType.SmallHatch, FlammableSourceType.SmallHole };
            foreach (var t in order)
            {
                var prefab = fire ? pem.GetFirePrefab(t) : pem.GetSmokePrefab(t);
                if (prefab == null) continue;
                var go = Instantiate(prefab, at.position, Quaternion.identity, at);
                go.name = fire ? "HitCam engine flames (" + t + ")" : "HitCam engine smoke (" + t + ")";
                var fps = go.GetComponentInChildren<IFireParticleSystem>();
                if (fps == null) { Destroy(go); continue; }
                if (fps.Orientation == FireParticleSystemOrientation.YOut) go.transform.up = up; else go.transform.forward = up;
                fps.SetAllRatios(0.05f);
                fps.Play();
                return fps;
            }
            return null;
        }

        void Update() { using (Perf.T("EngineFire.Update")) TimedUpdate(); }
        void TimedUpdate()
        {
            float t = Time.time - _t0;
            // grows over ~4 s, burns, dies down over ~10 s; the smoke goes on, thinning
            float f = t < 4f ? Mathf.SmoothStep(0.1f, 1f, t / 4f) : t < _burn ? 1f - 0.15f * Mathf.PerlinNoise(t * 0.3f, 0.5f) : Mathf.Clamp01(1f - (t - _burn) / 10f);
            try
            {
                if (_fire != null)
                {
                    if (f > 0.01f) _fire.SetAllRatios(_peak * f);
                    else { _fire.Stop(); _fire = null; }
                }
                if (_smoke != null)
                {
                    float s = t < _burn ? 0.4f + 0.6f * f : Mathf.Clamp01(1f - (t - _burn) / _smokeFor) * 0.6f;
                    if (s > 0.02f) _smoke.SetAllRatios(_peak * s);
                    else { _smoke.Stop(); _smoke = null; }
                }
            }
            catch { _fire = null; _smoke = null; }
            if (_fire == null && _smoke == null && t > _burn + 5f && !_ending) { _ending = true; Destroy(gameObject, 10f); }
        }
    }
}
