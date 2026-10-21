using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GHPC;
using GHPC.Audio;
using GHPC.Weaponry;
using GHPC.Weapons;
using GHPC.World;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using EventInstance = FMOD.Studio.EventInstance;
using EVENT_PROPERTY = FMOD.Studio.EVENT_PROPERTY;
using RuntimeManager = FMODUnity.RuntimeManager;
using StudioEventEmitter = FMODUnity.StudioEventEmitter;

namespace GHPCHitCam
{
    /// Explosions as loud as their TNT. GHPC plays every blast through FMOD with one event per kind of round (a 0.2 kg and a
    /// 4 kg HE shell sound the same), mixes them all within a few dB (measured: its vehicle detonation comes out under a
    /// small HE shell) and lets them fall off very little with distance.
    ///  - every detonation comes from the game's own Explosions.RegisterExplosion (point + TNT); a rack is the compartment's
    ///    call, which hands BlastEffectManager the same point and TNT in the same frame with a 0.3 fragment factor
    ///  - the game's explosion one-shots (FmodGenericAudioManager.StartOneShot*, StudioEventEmitter) are matched to their
    ///    detonation by position. Each event stands for a reference charge Wr (RefCharge: its rounds, and its measured level
    ///    in the game's mix); the charge that went off, W, scales it by the ratio of the peak overpressures at the listener,
    ///    P(W,d) / P(Wr,d) (Kingery-Bulmash, surface burst), and its audible range by (W/Wr)^(1/3) (Hopkinson-Cranz).
    ///    Times ExplosionLoudness. The game's distance curve stays as it is.
    ///  - a rack also gets a synthesised low boom (BoomSynth: N-wave of the blast's own length, ground thump, terrain echoes,
    ///    a rolling tail, low-passed by air absorption with distance), as does any charge of 10 kg and more; and if the game
    ///    played no explosion event for it, its own vehicle detonation event, pitched down by the same scaling
    ///  - all of it starts when the front arrives (distance / the game's speed of sound), with a camera kick (ShockKick)
    internal static class BlastSound
    {
        /// Share of a rack's TNT that reaches the open air as blast (the hull confines it; work goes into the hull and turret).
        const float HullFactor = 0.7f;
        /// Non-rack charges from this size get the boom layer too (big bombs, heavy artillery).
        const float BoomMinKg = 10f;
        /// What the vehicle detonation event stands for if the measured mix table (MixDb) misses it, in open-air TNT.
        const float VehicleDetRef = 0.9f;

        internal class Det
        {
            public Vector3 Pos;
            public float Tnt, T, Delay, Dist;
            public bool Rack, Started, FollowUp, TurretOff;
            /// The game's events matched to this blast (a rack's): their volume is set again once it is known whether the
            /// vehicle blew apart.
            public readonly List<(EventInstance inst, EvInfo info, string via, float pitch)> Events = new List<(EventInstance, EvInfo, string, float)>();
            public int Frame, Parts = 1, GameEvents;
            public string Name = "";
            public readonly HashSet<string> Probed = new HashSet<string>();
        }

        internal class EvInfo
        {
            public string Path = "";
            public float Min, Max, Wr = 1f;
            public string WrFrom = "";
            public bool Explosion, Burst;
        }

        struct Pending { public EventInstance Inst; public EvInfo Info; public Vector3 Pos; public int Frame; public string Via; }

        static readonly List<Det> _dets = new List<Det>();
        static readonly Dictionary<IntPtr, EvInfo> _info = new Dictionary<IntPtr, EvInfo>();
        static readonly List<Pending> _pending = new List<Pending>();
        static Dictionary<string, (float kg, string from)> _ref;
        /// Racks that went off lately (where, when): the first one on a vehicle is the big blast; the rest of its rounds
        /// going off after it are a fraction of it (most of the charge has gone), so they come out this much quieter.
        static readonly List<(Vector3 p, float t)> _racks = new List<(Vector3, float)>();
        const float FollowUpWindow = 60f;
        internal const float FollowUpGain = 0.35f;
        /// Parts the game threw off (turrets): where, when. The blast that does it is the vehicle's catastrophic explosion.
        static readonly List<(Vector3 p, float t)> _detaches = new List<(Vector3, float)>();
        /// A rack's sound waits this long at least, so a turret thrown off in the frames after the detonation is known.
        const float RackDecide = 0.12f;

        /// How big a blast is made to sound: (cap and gain on the game event's physical boost, its range factor, the
        /// synthesised boom's level and tail length). The huge one is kept for the vehicle that blows apart (its turret
        /// thrown off: the game's catastrophic explosion). A rack going off under a turret that stays on is a solid boom,
        /// about +6 dB over the game's own sound at most, half the low boom, a shorter tail. A vehicle's later blasts: less again.
        internal static (float cap, float gain, float kMax, float boom, float tail) Grade(Det d)
        {
            if (!d.Rack || d.TurretOff) return (8f, 1f, 6f, 1f, 1f);
            if (d.FollowUp) return (1.5f, FollowUpGain, 1.5f, FollowUpGain, 0.6f);
            return (2.2f, 1f, 1.8f, 0.5f, 0.7f);
        }

        static string GradeName(Det d) => !d.Rack ? "detonation" : d.TurretOff ? "CATASTROPHIC (turret off)" : d.FollowUp ? "follow-up rack" : "rack, turret stays on";

        /// The game threw a part off (Recorder's detach hook): the blast that did it is the big one. One that is already
        /// playing gets its events turned up and the full boom on top.
        internal static void OnDetach(Vector3 p)
        {
            float now = Time.time;
            _detaches.RemoveAll(x => now - x.t > 5f);
            _detaches.Add((p, now));
            foreach (var d in _dets)
            {
                if (!d.Rack || d.TurretOff || !d.Started || now - d.T > 1f || (d.Pos - p).sqrMagnitude > 15f * 15f) continue;
                d.TurretOff = true;
                Regrade(d);
                try
                {
                    Vector3 cam = CamPos(out bool ok);
                    if (!ok || !Settings.ExplosionSound.Value) continue;
                    float dist = Vector3.Distance(cam, d.Pos), w = EffKg(d);
                    string boom = BoomPlayer.Play(d, w, dist, BlastPhysics.PeakPa(w, dist), cam);
                    if (AutoTest.Active) HitCamMod.Log.Msg($"Explosion sound: the turret came off {now - d.T:0.00} s after the rack went: now CATASTROPHIC, events turned up, boom: {boom}");
                }
                catch (Exception e) { if (_warned++ < 5) HitCamMod.Log.Warning("Explosion sound: " + e.Message); }
            }
        }

        static void Regrade(Det d)
        {
            foreach (var (inst, info, via, pitch) in d.Events)
                if (inst.isValid()) Apply(inst, info, d, via, pitch, again: true);
        }
        static string _burstPath;
        static bool _burstTried;
        static List<string> _allEvents;
        static AccessTools.FieldRef<StudioEventEmitter, EventInstance> _emitterInst;
        static AccessTools.FieldRef<ImpactSFXManager, ImpactSFXManager.SimpleImpactInfo[]> _impactCfg;
        static int _warned, _probeLines;

        // self-test readouts
        public static Det LastRack;
        public static int ScaledEvents, OwnEvents, Booms;
        static float Loudness => Mathf.Clamp(Settings.ExplosionLoudness.Value, 0.25f, 4f);

        public static void Install(HarmonyLib.Harmony h)
        {
            Settings.InitBlast();
            try { _emitterInst = AccessTools.FieldRefAccess<StudioEventEmitter, EventInstance>("instance"); } catch { }
            try { _impactCfg = AccessTools.FieldRefAccess<ImpactSFXManager, ImpactSFXManager.SimpleImpactInfo[]>("SimpleImpactsConfig"); } catch { }
            Patch(h, AccessTools.Method(typeof(FmodGenericAudioManager), nameof(FmodGenericAudioManager.StartOneShot)), nameof(OneShotPrefix), null);
            Patch(h, AccessTools.Method(typeof(FmodGenericAudioManager), nameof(FmodGenericAudioManager.StartOneShotWithCameraSoundDelay)), nameof(DelayedOneShotPrefix), null);
            if (_emitterInst != null) Patch(h, AccessTools.Method(typeof(StudioEventEmitter), "PlayInstance"), null, nameof(EmitterPostfix));
            Patch(h, AccessTools.Method(typeof(BlastEffectManager), nameof(BlastEffectManager.HandleExplosiveBlast)), nameof(BlastPrefix), null);
            GHPC.Effects.Explosions.AddListener(new Listener());
            ShockKick.Install();
        }

        static void Patch(HarmonyLib.Harmony h, System.Reflection.MethodBase target, string prefix, string postfix)
        {
            if (target == null) { HitCamMod.Log.Warning("Explosion sound: patch target for " + (prefix ?? postfix) + " not found (game update?)"); return; }
            try
            {
                h.Patch(target,
                    prefix == null ? null : new HarmonyMethod(typeof(BlastSound).GetMethod(prefix, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)),
                    postfix == null ? null : new HarmonyMethod(typeof(BlastSound).GetMethod(postfix, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            }
            catch (Exception e) { HitCamMod.Log.Warning("Explosion sound: could not patch " + target.Name + ": " + e.Message); }
        }

        public static void Reset()
        {
            _dets.Clear();
            _racks.Clear();
            _detaches.Clear();
            _pending.Clear();
            _info.Clear();      // reference charges depend on the mission's rounds
            _ref = null;
            _burstPath = null;
            _burstTried = false;
            ShockKick.Reset();
            BoomPlayer.Reset();
        }

        class Listener : GHPC.Effects.IExplosionListener
        {
            public void OnExplosion(Vector3 worldPosition, float tntEquivalent)
            {
                try { BlastSound.OnExplosion(worldPosition, tntEquivalent); }
                catch (Exception e) { if (_warned++ < 5) HitCamMod.Log.Warning("Explosion sound: " + e); }
            }
        }

        // ------------------------------------------------------------------ detonations

        static Vector3 _blastAt;
        static float _blastTnt, _blastFrag = 1f;
        static int _blastFrame = -1;

        /// The compartment calls this right before it registers a rack's detonation (same point, same TNT, frag factor 0.3);
        /// a round calls it right after registering its own.
        static void BlastPrefix(Vector3 blastWorldPos, float blastTntEquivalenceKg, float fragScalingOverride)
        {
            _blastAt = blastWorldPos;
            _blastTnt = blastTntEquivalenceKg;
            _blastFrag = fragScalingOverride;
            _blastFrame = Time.frameCount;
        }

        static Vector3 CamPos(out bool ok)
        {
            var c = GHPC.Camera.CameraManager.MainCam;
            ok = c != null;
            return ok ? c.transform.position : Vector3.zero;
        }

        static void OnExplosion(Vector3 p, float tnt)
        {
            if (!Settings.ExplosionSound.Value || tnt <= 0f) return;
            Vector3 cam = CamPos(out bool ok);
            if (!ok) return;
            bool rack = _blastFrame == Time.frameCount && _blastFrag < 0.5f && Mathf.Abs(_blastTnt - tnt) < 1e-3f && (_blastAt - p).sqrMagnitude < 0.01f;
            if (rack)
                // the clusters of one rack going off together are one blast (if its sound hasn't reached the camera yet)
                foreach (var d in _dets)
                    if (d.Rack && !d.Started && Time.time - d.T < 0.25f && (d.Pos - p).sqrMagnitude < 64f)
                    {
                        d.Pos = (d.Pos * d.Tnt + p * tnt) / (d.Tnt + tnt);
                        d.Tnt += tnt;
                        d.Parts++;
                        return;
                    }
            float dist = Vector3.Distance(cam, p);
            if (AutoTest.Opt("kickcap", false) && !rack) MelonCoroutines.Start(AutoTest.ScreenFrames(AutoTest.Opt("kicktransform", false) ? "kick_old_before" : "kick_new_before", 0f));
            var det = new Det { Pos = p, Tnt = tnt, Rack = rack, T = Time.time, Frame = Time.frameCount, Dist = dist, Delay = WorldEnvironmentManager.GetSoundDelay(dist) };
            if (rack)
            {
                det.Name = UnitNear(p);
                LastRack = det;
                float now = Time.time;
                _racks.RemoveAll(r => now - r.t > FollowUpWindow || now < r.t);
                foreach (var r in _racks) if ((r.p - p).sqrMagnitude < 15f * 15f) { det.FollowUp = true; break; }
                _racks.Add((p, now));
            }
            _dets.Add(det);
            // game sounds that started before the detonation was registered this frame (an impact right at the camera)
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var q = _pending[i];
                float r = MatchRadius(det);
                if (Time.frameCount - q.Frame <= 1 && (q.Pos - p).sqrMagnitude < r * r && q.Inst.isValid())
                {
                    Apply(q.Inst, q.Info, det, q.Via + ", matched late");
                    _pending.RemoveAt(i);
                }
            }
        }

        static float EffKg(Det d) => d.Rack ? d.Tnt * HullFactor : d.Tnt;
        static float MatchRadius(Det d) => d.Rack ? 12f : 4f;

        static string UnitNear(Vector3 p)
        {
            try
            {
                var sum = SceneUnitsManager.Instance;
                if (sum == null) return "";
                Unit best = null;
                float bd = 100f;
                foreach (var u in sum.AllUnitsInScene)
                {
                    if (u == null) continue;
                    float d = (u.transform.position - p).sqrMagnitude;
                    if (d < bd) { bd = d; best = u; }
                }
                return best != null ? Recorder.SafeName(best) : "";
            }
            catch { return ""; }
        }

        public static void Update()
        {
            if (_dets.Count > 0)
            {
                float now = Time.time;
                for (int i = _dets.Count - 1; i >= 0; i--)
                {
                    var d = _dets[i];
                    if (!d.Started && now - d.T >= (d.Rack ? Mathf.Max(d.Delay, RackDecide) : d.Delay))
                    {
                        d.Started = true;
                        try { Arrive(d); }
                        catch (Exception e) { if (_warned++ < 5) HitCamMod.Log.Warning("Explosion sound: " + e); }
                    }
                    if (now - d.T > d.Delay + 4f || now < d.T - 1f) _dets.RemoveAt(i);
                }
            }
            if (_pending.Count > 0) _pending.RemoveAll(q => Time.frameCount - q.Frame > 2);
            BoomPlayer.Update();
        }

        /// The blast front reaches the camera: kick, the vehicle detonation event and the boom.
        static void Arrive(Det d)
        {
            if (d.Rack && !d.TurretOff)
            {
                foreach (var x in _detaches)
                    if (x.t >= d.T - 0.3f && (x.p - d.Pos).sqrMagnitude < 15f * 15f) { d.TurretOff = true; break; }
                if (d.TurretOff) Regrade(d);
            }
            Vector3 cam = CamPos(out bool ok);
            if (!ok) return;
            float dist = Vector3.Distance(cam, d.Pos);
            float w = EffKg(d);
            float p = BlastPhysics.PeakPa(w, dist), tp = BlastPhysics.PositiveSeconds(w, dist), imp = BlastPhysics.Impulse(w, dist);
            string kick = Settings.ShockwaveShake.Value ? ShockKick.Add(d.Pos, cam, p, imp, w, tp) : "shake off";
            bool big = d.Rack || d.Tnt >= BoomMinKg;
            string own = "", boom = "";
            if (d.Rack && d.GameEvents == 0) own = PlayOwnEvent(d, w, dist);
            if (big) boom = BoomPlayer.Play(d, w, dist, p, cam);
            if (!big && !AutoTest.Active) return;
            if (!big && d.Tnt < 0.1f) return;
            float shell = BlastPhysics.PeakPa(0.75f, dist);
            HitCamMod.Log.Msg($"Explosion sound: {(d.Rack ? "AMMO RACK, " + GradeName(d) + (d.Name.Length > 0 ? ", of " + d.Name : "") + (d.Parts > 1 ? $" ({d.Parts} parts)" : "") : "detonation")} {d.Tnt:0.##} kg TNT"
                + (d.Rack ? $" ({w:0.#} kg in the open air)" : "") + $" at {dist:0} m, front arrived after {Time.time - d.T:0.00} s (sound delay {d.Delay:0.00} s):"
                + $" peak {p:0} Pa = {BlastPhysics.Db(p):0.0} dB SPL (a 0.75 kg HE shell there: {BlastPhysics.Db(shell):0.0} dB), positive phase {tp * 1000f:0.0} ms, impulse {imp:0.00} Pa*s;"
                + $" game events scaled {d.GameEvents}" + (own.Length > 0 ? "; own event: " + own : "") + (boom.Length > 0 ? "; boom: " + boom : "") + "; " + kick);
        }

        // ------------------------------------------------------------------ the game's own explosion events

        static void OneShotPrefix(EventInstance __0, Vector3 __1) => OnGameEvent(__0, __1, "one-shot");
        static void DelayedOneShotPrefix(EventInstance __0, Vector3 __1) => OnGameEvent(__0, __1, "delayed one-shot");

        static void EmitterPostfix(StudioEventEmitter __instance)
        {
            if (__instance == null || _emitterInst == null) return;
            OnGameEvent(_emitterInst(__instance), __instance.transform.position, "emitter " + __instance.name);
        }

        static void OnGameEvent(EventInstance inst, Vector3 pos, string via)
        {
            if (!Settings.ExplosionSound.Value) return;
            try
            {
                var info = Describe(inst);
                if (info == null) return;
                if (AutoTest.Active) Probe(info, pos, via);
                if (!info.Explosion && !info.Burst) return;
                var det = Match(pos);
                if (det == null) { _pending.Add(new Pending { Inst = inst, Info = info, Pos = pos, Frame = Time.frameCount, Via = via }); return; }
                Apply(inst, info, det, via);
            }
            catch (Exception e) { if (_warned++ < 5) HitCamMod.Log.Warning("Explosion sound: event hook: " + e.Message); }
        }

        /// Self-test: what the game plays around a detonation (to see which events a cook-off uses).
        static void Probe(EvInfo info, Vector3 pos, string via)
        {
            if (_probeLines > 80) return;
            foreach (var d in _dets)
            {
                if (Time.time - d.T > d.Delay + 20f || (d.Pos - pos).sqrMagnitude > 400f) continue;
                if (!d.Probed.Add(info.Path)) return;
                _probeLines++;
                Vector3 cam = CamPos(out _);
                HitCamMod.Log.Msg($"Explosion sound probe: {via} {info.Path} at {Vector3.Distance(cam, pos):0} m from the camera, {Time.time - d.T:0.00} s after a {d.Tnt:0.##} kg {(d.Rack ? "rack" : "")} detonation {Vector3.Distance(d.Pos, pos):0.0} m away (event min/max {info.Min:0.#}/{info.Max:0.#} m)");
                return;
            }
        }

        static Det Match(Vector3 pos)
        {
            Det best = null;
            float bestD = float.MaxValue;
            foreach (var d in _dets)
            {
                float age = Time.time - d.T;
                if (age < -0.01f || age > d.Delay + 2f) continue;
                float r = MatchRadius(d);
                float dd = (d.Pos - pos).sqrMagnitude;
                if (dd < r * r && dd < bestD) { best = d; bestD = dd; }
            }
            return best;
        }

        static EvInfo Describe(EventInstance inst)
        {
            if (!inst.isValid() || inst.getDescription(out var desc) != FMOD.RESULT.OK) return null;
            if (_info.TryGetValue(desc.handle, out var info)) return info;
            desc.getPath(out string path);
            desc.getMinMaxDistance(out float min, out float max);
            info = new EvInfo { Path = path ?? "", Min = min, Max = max };
            string pl = info.Path.ToLowerInvariant();
            info.Explosion = pl.StartsWith("event:/explosions/") && !pl.Contains("smoke");
            info.Burst = !info.Explosion && info.Path == BurstPath();
            if (info.Explosion || info.Burst) (info.Wr, info.WrFrom) = RefCharge(info.Path);
            _info[desc.handle] = info;
            return info;
        }

        static float _burstTriedAt = -99f;
        /// The tables the first blast of a mission would otherwise read in its own frame (Warmup).
        internal static void Prewarm()
        {
            if (!Settings.ExplosionSound.Value) return;
            if (_ref == null) BuildRefs();
            BurstPath();
            OwnEventPath();
        }

        static string BurstPath()
        {
            // the vehicle audio manager only exists in a mission: look again now and then until it does
            if (_burstPath != null || (_burstTried && Time.unscaledTime - _burstTriedAt < 5f)) return _burstPath;
            _burstTried = true;
            _burstTriedAt = Time.unscaledTime;
            try
            {
                var r = VehicleAudioManager.FireBurstEvent;
                if (!r.IsNull) RuntimeManager.GetEventDescription(r).getPath(out _burstPath);
            }
            catch { }
            if (_burstPath != null)
                foreach (var info in _info.Values)
                    if (info.Path == _burstPath && !info.Burst) { info.Burst = true; (info.Wr, info.WrFrom) = RefCharge(info.Path); }
            return _burstPath;
        }

        /// The game's explosion events, measured in the game (self-test cal:, 2026-10-20, two runs: each event's own FMOD
        /// output, peak, at 1 km, against the HE event (HiEX_Small) at the same spot). GHPC mixes all of them within a few
        /// dB: its vehicle detonation comes out 1.4 dB under a small HE shell, a 20 kg artillery shell 1.3 dB over it and a
        /// 140 kg bomb 2.1 dB over it. Every event also has a limiter at 0 dBFS on its own output, so a boost can't clip.
        static readonly Dictionary<string, float> MixDb = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            { "Explosion_HiEX_Small", 0f }, { "Explosion_VehicleDet", -1.4f }, { "Explosion_Artillary", 1.3f }, { "Explosion_Bomb", 2.1f },
            { "Explosion_Missile", 1.8f }, { "Explosion_HiEX", 1.1f }, { "Explosion_Rockets", -1.2f }, { "Explosion_HEAT", -3.5f },
            { "Explosion_Mortar", -3.5f }, { "Explosion_AutoCanon", -3.8f },
        };
        /// The HE event's own level vs distance (peak, dBFS), measured the same way: GHPC lets its explosions fall off only
        /// ~5 dB from 150 m to 1 km and not at all beyond (physics: ~19 and ~10 dB; its 125 mm gun falls 12 and 8). Kept as
        /// it is: only the charge is scaled.
        static readonly (float m, float db)[] AnchorCurve = { (150f, -0.5f), (300f, -2.0f), (1000f, -5.4f), (3000f, -5.8f) };
        /// Charge the HE event stands for: the geometric mean of the game's rounds that play it (log), else this.
        static float _anchorKg = 1.41f;

        static float AnchorDb(float dist)
        {
            var c = AnchorCurve;
            if (dist <= c[0].m) return c[0].db;
            for (int i = 1; i < c.Length; i++)
                if (dist <= c[i].m)
                    return Mathf.Lerp(c[i - 1].db, c[i].db, Mathf.Log(dist / c[i - 1].m) / Mathf.Log(c[i].m / c[i - 1].m));
            return c[c.Length - 1].db;
        }

        static string EventName(string path) { int i = path.LastIndexOf('/'); return i >= 0 ? path.Substring(i + 1) : path; }

        /// The charge an event's level stands for. From the game's rounds: the geometric mean TNT of those that play it
        /// (several impact types can share one event). From the game's mix: the charge whose overpressure, against the HE
        /// event's rounds, matches the event's measured level against the HE event (far field, peak ~ W^0.37 at a given
        /// distance), so one physical scale covers every event. The smaller of the two wins: physics only ever adds to the
        /// game's own level for the rounds an event was made for (the autocannon event is mixed like a 0.4 kg charge; its
        /// 20-50 g shells keep the game's level), and a vehicle detonation, which no round plays, is measured only.
        static (float, string) RefCharge(string path)
        {
            if (_ref == null) BuildRefs();
            bool rounds = _ref.TryGetValue(path, out var r);
            if (MixDb.TryGetValue(EventName(path), out float db))
            {
                float mix = _anchorKg * Mathf.Pow(10f, db / (20f * FarExp));
                if (!rounds || mix < r.Item1)
                    return (mix, $"its level in the game's mix, {db:+0.0;-0.0} dB vs the HE event of {_anchorKg:0.##} kg" + (rounds ? $"; its rounds: {r.Item1:0.###} kg" : ""));
                return (r.Item1, r.Item2 + $"; its mix level alone: {mix:0.###} kg");
            }
            if (rounds) return r;
            string p = path.ToLowerInvariant();
            if (p.Contains("vehicledet")) return (VehicleDetRef, "the vehicle detonation event, as measured");
            if (path == _burstPath) return (2f, "propellant burst");
            if (p.Contains("bomb")) return (100f, "bomb, by name");
            if (p.Contains("artil")) return (6f, "artillery shell, by name");
            if (p.Contains("rocket")) return (5f, "rocket, by name");
            if (p.Contains("missile")) return (3f, "missile, by name");
            if (p.Contains("mortar")) return (1.5f, "mortar bomb, by name");
            if (p.Contains("autocanon") || p.Contains("autocannon")) return (0.03f, "autocannon shell, by name");
            if (p.Contains("small")) return (0.2f, "small HE, by name");
            if (p.Contains("heat")) return (0.8f, "HEAT, by name");
            return (1.5f, "HE shell, by name");
        }

        static void BuildRefs()
        {
            _ref = new Dictionary<string, (float, string)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var mgr = ImpactSFXManager.Instance;
                var cfg = mgr != null && _impactCfg != null ? _impactCfg(mgr) : null;
                if (cfg == null) return;
                var ammo = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>().Where(a => a != null && a.AmmoType != null).Select(a => a.AmmoType)
                    .GroupBy(a => a.Name).Select(g => g.First()).Where(a => a.TntEquivalentKg > 0.001f).ToList();
                var lines = new List<string>();
                foreach (var g in cfg.Where(c => c != null && !string.IsNullOrEmpty(c.FmodEvent)).GroupBy(c => c.FmodEvent))
                {
                    var types = g.Select(c => c.Type).ToList();
                    var rounds = ammo.Where(a => types.Contains(a.ImpactAudio)).ToList();
                    if (rounds.Count == 0) { lines.Add($"{g.Key} ({string.Join("/", types)}): no rounds"); continue; }
                    float gm = Mathf.Exp(rounds.Average(a => Mathf.Log(a.TntEquivalentKg)));
                    string from = $"geometric mean of {rounds.Count} rounds ({string.Join(", ", rounds.OrderBy(a => a.TntEquivalentKg).Select(a => $"{a.Name} {a.TntEquivalentKg:0.###}").Take(8))}{(rounds.Count > 8 ? ", ..." : "")})";
                    _ref[g.Key] = (gm, from);
                    if (EventName(g.Key).Equals("Explosion_HiEX_Small", StringComparison.OrdinalIgnoreCase)) _anchorKg = gm;
                    lines.Add($"{g.Key} ({string.Join("/", types)}) = {gm:0.###} kg, {from}");
                }
                HitCamMod.Log.Msg("Explosion sound: reference charges of the game's impact events:\n  " + string.Join("\n  ", lines));
            }
            catch (Exception e) { HitCamMod.Log.Warning("Explosion sound: reference charges: " + e.Message); }
        }

        /// Far-field exponent of peak overpressure in the charge at a fixed distance (beyond Z ~ 200: P ~ W^(1.1/3)).
        const float FarExp = 1.1f / 3f;
        /// One game event instance for this detonation. The event plays as it would for its reference charge Wr; the charge
        /// that went off is W: gain = P(W, d) / P(Wr, d), the ratio of the two peak overpressures at the listener
        /// (Kingery-Bulmash), times ExplosionLoudness. Its audible range (3D max distance) grows as k = (W/Wr)^(1/3)
        /// (Hopkinson-Cranz); a smaller charge keeps the event's range. The event's own limiter (0 dBFS, measured) takes what
        /// goes over: near, a big charge comes out at the game's loudest explosion level instead of clipping.
        static void Apply(EventInstance inst, EvInfo info, Det det, string via, float pitch = 1f, bool again = false)
        {
            if (det.Rack && !again) det.Events.Add((inst, info, via, pitch));
            Vector3 cam = CamPos(out _);
            float dist = Mathf.Max(Vector3.Distance(cam, det.Pos), 1f);
            float w = EffKg(det);
            float wr = info.Wr;
            float k = Mathf.Clamp(Mathf.Pow(w / wr, 1f / 3f), 0.4f, 6f);
            float phys = Mathf.Clamp(BlastPhysics.PeakPa(w, dist) / BlastPhysics.PeakPa(wr, dist), 0.15f, 8f);
            var grade = Grade(det);
            phys = Mathf.Min(phys, grade.cap) * grade.gain;
            k = Mathf.Min(k, grade.kMax);
            float ev = EventLevel(info.Path, dist);
            float vol = phys * Loudness;
            // near, a big boost only drives the event's limiter: its peak stays at full scale while its long recorded tail
            // comes up by the whole boost (a close rack sounded like a long rolling roar). Up close the blast is one huge
            // bang, the terrain's echoes far under it; far off the rolling tail is what you hear. So the drive into the
            // limiter is held to +3 dB close and allowed up to +9 dB far.
            float drive = Mathf.Pow(10f, Mathf.Lerp(3f, 9f, BoomSynth.Far(dist)) / 20f);
            vol = Mathf.Min(vol, Mathf.Max(drive / Mathf.Max(ev, 1e-3f), 1f));
            float level = ev * vol;    // its own output before its limiter
            float max = Mathf.Max(info.Max, info.Min + 1f) * Mathf.Max(k, 1f);
            if (k > 1f) inst.setProperty(EVENT_PROPERTY.MAXIMUM_DISTANCE, max);
            inst.setVolume(vol);
            if (pitch != 1f) inst.setPitch(pitch);
            if (again) { if (AutoTest.Active) HitCamMod.Log.Msg($"Explosion sound: {via} {info.Path} set again for {GradeName(det)}: gain {vol:0.00}"); return; }
            if (via != "own" && info.Explosion) det.GameEvents++;
            ScaledEvents++;
            if (!AutoTest.Active && !det.Rack && det.Tnt < BoomMinKg) return;
            if (inst.getProperty(EVENT_PROPERTY.MAXIMUM_DISTANCE, out float rmax) != FMOD.RESULT.OK || rmax < 0f) rmax = info.Max;   // -1: not overridden
            inst.getVolume(out float rvol);
            HitCamMod.Log.Msg($"Explosion sound: {via} {info.Path}: {det.Tnt:0.###} kg{(det.Rack ? " rack" : "")} ({w:0.###} kg open air) vs the event's {wr:0.###} kg ({info.WrFrom}):"
                + $" overpressure ratio at {dist:0} m {phys:0.00} ({Db(phys):+0.0;-0.0} dB) x loudness {Loudness:0.0}"
                + $" -> gain {rvol:0.00} ({Db(rvol):+0.0;-0.0} dB, its output ~{Db(level):+0.0;-0.0} dBFS{(level > 1f ? ", into its limiter" : "")}); max distance {info.Max:0} -> {rmax:0} m (k {k:0.00}){(pitch != 1f ? $", pitch {pitch:0.00}" : "")};"
                + $" sound delay {det.Delay:0.00} s, {Time.time - det.T:0.00} s after the detonation");
        }

        static float Db(float a) => 20f * Mathf.Log10(Mathf.Max(a, 1e-6f));

        /// An event's own output peak (full scale, before the master volume) at this distance, as measured.
        internal static float EventLevel(string path, float dist) => Mathf.Pow(10f, (AnchorDb(dist) + (MixDb.TryGetValue(EventName(path), out float mdb) ? mdb : 0f)) / 20f);

        /// The same physical scale for the boom: the HE event's level at this distance times the overpressure ratio.
        internal static float AnchorLevel(float w, float dist) =>
            Mathf.Pow(10f, AnchorDb(dist) / 20f) * BlastPhysics.PeakPa(w, Mathf.Max(dist, 1f)) / BlastPhysics.PeakPa(_anchorKg, Mathf.Max(dist, 1f));

        /// The game's own vehicle detonation event (else its biggest explosion) for a rack, which the game plays nothing for
        /// beyond 300 m. Pitched down by the same scaling: a blast's length grows as W^(1/3), its pitch falls as W^(-1/3).
        static string PlayOwnEvent(Det d, float w, float dist)
        {
            string path = OwnEventPath();
            if (path == null) return "no game event to use";
            var inst = RuntimeManager.CreateInstance(path);
            if (!inst.isValid()) return "could not create " + path;
            inst.set3DAttributes(FMODUnity.RuntimeUtils.To3DAttributes(d.Pos));
            var info = Describe(inst);
            float pitch = Mathf.Clamp(Mathf.Pow(info.Wr / Mathf.Max(w, 0.01f), 1f / 3f), 0.55f, 1f);
            pitch = Mathf.Lerp(Mathf.Max(pitch, 0.85f), pitch, BoomSynth.Far(dist));   // pitching down stretches its tail: less of it close
            Apply(inst, info, d, "own", pitch);
            inst.start();
            inst.release();
            OwnEvents++;
            return $"{path} pitch {pitch:0.00}";
        }

        static string _ownPath;
        static bool _ownTried;
        static string OwnEventPath()
        {
            if (_ownTried) return _ownPath;
            _ownTried = true;
            var all = AllEvents();
            _ownPath = all.FirstOrDefault(p => p.IndexOf("VehicleDet", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? all.FirstOrDefault(p => p.IndexOf("Explosions/", StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("Bomb", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? all.FirstOrDefault(p => p.IndexOf("Explosions/", StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("Artil", StringComparison.OrdinalIgnoreCase) >= 0);
            HitCamMod.Log.Msg("Explosion sound: rack detonation event = " + (_ownPath ?? "(none found)"));
            return _ownPath;
        }

        /// Every event path in the loaded banks (and, in the self-test, a dump of them with their 3D distances).
        static List<string> AllEvents()
        {
            if (_allEvents != null) return _allEvents;
            _allEvents = new List<string>();
            var dump = new List<string>();
            try
            {
                RuntimeManager.StudioSystem.getBankList(out var banks);
                foreach (var b in banks ?? new FMOD.Studio.Bank[0])
                {
                    b.getPath(out string bp);
                    if (b.getEventList(out var evs) != FMOD.RESULT.OK || evs == null) continue;
                    foreach (var e in evs)
                    {
                        e.getPath(out string p);
                        if (string.IsNullOrEmpty(p)) continue;
                        _allEvents.Add(p);
                        if (!AutoTest.Active) continue;
                        e.getMinMaxDistance(out float mn, out float mx);
                        e.getLength(out int len);
                        e.is3D(out bool is3d);
                        e.isOneshot(out bool one);
                        var pars = new List<string>();
                        if (e.getParameterDescriptionCount(out int pc) == FMOD.RESULT.OK)
                            for (int i = 0; i < pc; i++)
                                if (e.getParameterDescriptionByIndex(i, out var pd) == FMOD.RESULT.OK) pars.Add($"{(string)pd.name}[{pd.type} {pd.minimum:0.##}..{pd.maximum:0.##}]");
                        dump.Add($"{p}\tbank {bp}\tmin {mn:0.#} max {mx:0.#} m\tlength {len} ms\t{(is3d ? "3D" : "2D")}{(one ? " oneshot" : "")}\t{string.Join(" ", pars)}");
                    }
                }
                if (AutoTest.Active)
                {
                    dump.Sort();
                    File.WriteAllLines(Path.Combine(HitCamMod.DataDir, "fmod_events.txt"), dump);
                    HitCamMod.Log.Msg($"Explosion sound: {dump.Count} FMOD events written to fmod_events.txt; explosions: " + string.Join(" | ", dump.Where(l => l.StartsWith("event:/Explosions", StringComparison.OrdinalIgnoreCase)).Select(l => l.Split('\t')[0].Substring(18) + " " + l.Split('\t')[2])));
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Explosion sound: event list: " + e.Message); }
            return _allEvents;
        }

        // ------------------------------------------------------------------ self-test

        /// near:<arsenal round>:<metres>: a round fired into the ground that far in front of the camera.
        public static IEnumerator NearShot(string[] spec, Unit player)
        {
            AllEvents();
            string name = spec.Length > 1 ? spec[1] : "HE 76 mm";
            float dist = spec.Length > 2 && float.TryParse(spec[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float dd) ? dd : 150f;
            var e = Arsenal.Find(name);
            var cam = GHPC.Camera.CameraManager.MainCam;
            if (e == null || cam == null) { HitCamMod.Log.Warning($"Explosion sound self-test: no round '{name}' or no camera"); yield break; }
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 aim = GHPC.Utility.CodeUtils.GetPositionOnTerrain(cam.transform.position + fwd * dist + Vector3.up * 800f, out bool ok, 3000f);
            if (!ok) aim = cam.transform.position + fwd * dist + Vector3.down * 2f;
            Arsenal.PrepareForFire(e);
            var round = LiveRoundUtility.GetNewLiveRound(e.Ammo);
            round.Info = e.Ammo;
            round.Guided = false;
            round.UseGravity = false;
            round.CurrentSpeed = e.Ammo.MuzzleVelocity;
            round.MaxSpeed = e.Ammo.MuzzleVelocity;
            round.Shooter = player;
            Vector3 from = aim + (Vector3.up * 0.8f - fwd * 0.6f).normalized * 30f;
            round.transform.position = from;
            round.transform.rotation = Quaternion.LookRotation((aim - from).normalized);
            round.Init(null, null);
            var slot = GHPC.Camera.CameraSlot.ActiveInstance;
            HitCamMod.Log.Msg($"Explosion sound self-test: {e.Ammo.Name} ({e.Ammo.TntEquivalentKg:0.##} kg TNT) fired into the ground {Vector3.Distance(cam.transform.position, aim):0} m from the camera "
                + $"(camera {cam.name} fov {cam.fieldOfView:0.#}, slot {(slot != null ? slot.name : "none")}, listener volume {AudioListener.volume:0.00})");
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < dist / 340f + 3.5f) yield return null;
        }

        /// cal:<metres>[:<metres>...]: the game's 125 mm gun, its explosion events (as the game plays them and scaled for a
        /// charge) and the boom, one after another at that distance in front of the camera. Each is measured on its own FMOD
        /// channel group (post-fader, before the buses and the master volume) and on the master output (after everything,
        /// minus what was playing just before); the boom on its Unity source (after the listener volume = master volume).
        /// At the first distance also every other explosion event, for the mix table (MixDb).
        public static IEnumerator Calibrate(string[] spec)
        {
            AllEvents();
            var dists = new List<float>();
            for (int i = 1; i < spec.Length; i++)
                if (float.TryParse(spec[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float dd)) dists.Add(dd);
            if (dists.Count == 0) dists.Add(1000f);
            var cam = GHPC.Camera.CameraManager.MainCam;
            if (cam == null) yield break;
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
            string vd = OwnEventPath() ?? "";
            const string he = "event:/Explosions/HiEX_Small/Explosion_HiEX_Small";
            FMOD.DSP master = default;
            bool masterOn = false;
            try
            {
                if (RuntimeManager.CoreSystem.getMasterChannelGroup(out var mcg) == FMOD.RESULT.OK && mcg.getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.HEAD, out master) == FMOD.RESULT.OK)
                    masterOn = master.setMeteringEnabled(false, true) == FMOD.RESULT.OK;
            }
            catch { }
            HitCamMod.Log.Msg($"Explosion sound calibration: master volume (AudioListener) {AudioListener.volume:0.00}, master meter {(masterOn ? "on" : "off")}, reference charges: HE {_anchorKg:0.##} kg, vehicle detonation {RefCharge(vd).Item1:0.##} kg");
            for (int di = 0; di < dists.Count; di++)
            {
                float dist = dists[di];
                Vector3 pos = cam.transform.position + fwd * dist;
                var cases = new List<(string label, string path, float kg, bool rack, int reps)>
                {
                    ("125 mm gun (the game's)", "event:/Weapons/canon_125mm-2A46", 0f, false, 2),
                    ("HE as the game plays it", he, 0f, false, 3),
                    ("HE for a 0.75 kg shell", he, 0.75f, false, 1),
                    ("HE for a 3.97 kg 3OF26", he, 3.97f, false, 1),
                    ("vehicle detonation as the game plays it", vd, 0f, false, 2),
                    ("vehicle detonation for a 32.6 kg rack", vd, 32.6f, true, 2),
                    ("boom for a 32.6 kg rack", "boom", 32.6f, true, 1),
                };
                if (di == 0)
                    foreach (var p in _allEvents.Where(p => p.StartsWith("event:/Explosions/", StringComparison.OrdinalIgnoreCase) && !p.Equals(he, StringComparison.OrdinalIgnoreCase) && !p.Equals(vd, StringComparison.OrdinalIgnoreCase)))
                        cases.Add((EventName(p) + " as the game plays it", p, 0f, false, 2));
                foreach (var c in cases)
                {
                    var own = new List<float>();
                    var outp = new List<float>();
                    string note = "";
                    for (int rep = 0; rep < c.reps; rep++)
                    {
                        var det = new Det { Pos = pos, Tnt = Mathf.Max(c.kg, 0.01f), Rack = c.rack, T = Time.time, Frame = Time.frameCount, Dist = dist, Delay = 0f, Started = true };
                        // what plays already (engines, wind): the master's level just before
                        float baseline = 0f, t0 = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - t0 < 0.4f)
                        {
                            if (masterOn) baseline = Mathf.Max(baseline, MeterPeak(master));
                            yield return null;
                        }
                        float peak = 0f, mpeak = 0f;
                        if (c.path == "boom")
                        {
                            float w = EffKg(det);
                            note = BoomPlayer.Play(det, w, dist, BlastPhysics.PeakPa(w, dist), cam.transform.position);
                            t0 = Time.realtimeSinceStartup;
                            while (Time.realtimeSinceStartup - t0 < 3.5f) yield return null;
                            peak = BoomPlayer.LastPeak;
                            mpeak = peak;
                        }
                        else
                        {
                            var inst = RuntimeManager.CreateInstance(c.path);
                            if (!inst.isValid()) { note = "no such event"; break; }
                            inst.set3DAttributes(FMODUnity.RuntimeUtils.To3DAttributes(pos));
                            var info = Describe(inst);
                            if (c.kg > 0f) Apply(inst, info, det, "calibration");
                            inst.start();
                            FMOD.DSP dsp = default;
                            bool metering = false;
                            t0 = Time.realtimeSinceStartup;
                            while (Time.realtimeSinceStartup - t0 < 2.6f)
                            {
                                if (!metering && inst.getChannelGroup(out var cg) == FMOD.RESULT.OK && cg.getDSP(FMOD.CHANNELCONTROL_DSP_INDEX.HEAD, out dsp) == FMOD.RESULT.OK)
                                    metering = dsp.setMeteringEnabled(false, true) == FMOD.RESULT.OK;
                                if (metering) peak = Mathf.Max(peak, MeterPeak(dsp));
                                if (masterOn) mpeak = Mathf.Max(mpeak, MeterPeak(master));
                                yield return null;
                            }
                            inst.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
                            inst.release();
                            if (!metering) note = "(no event meter)";
                        }
                        own.Add(Dbfs(peak));
                        outp.Add(mpeak > baseline * 1.05f ? Dbfs(mpeak) : float.NaN);
                        t0 = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - t0 < 0.3f) yield return null;
                    }
                    if (own.Count == 0) { HitCamMod.Log.Warning($"Explosion sound calibration: {c.label}: {note}"); continue; }
                    string Fmt(List<float> v) => v.Count == 0 || v.All(float.IsNaN) ? "-" : $"{v.Where(x => !float.IsNaN(x)).Average():0.0}" + (v.Count > 1 ? $" ({string.Join(" / ", v.Select(x => float.IsNaN(x) ? "-" : x.ToString("0.0")))})" : "");
                    HitCamMod.Log.Msg($"Explosion sound calibration at {dist:0} m: {c.label}: {(c.path == "boom" ? "Unity source" : "own output")} peak {Fmt(own)} dBFS, master output {Fmt(outp)} dBFS" + (note.Length > 0 ? "; " + note : ""));
                }
            }
            if (masterOn) master.setMeteringEnabled(false, false);
        }

        static float MeterPeak(FMOD.DSP dsp)
        {
            float m = 0f;
            if (dsp.getMeteringInfo(IntPtr.Zero, out FMOD.DSP_METERING_INFO mi) == FMOD.RESULT.OK)
                for (int ch = 0; ch < Mathf.Min((int)mi.numchannels, 8); ch++) m = Mathf.Max(m, mi.peaklevel[ch]);
            return m;
        }

        static float Dbfs(float a) => 20f * Mathf.Log10(Mathf.Max(a, 1e-7f));

        /// One line for the end of the self-test.
        public static string Summary() => $"explosion events scaled {ScaledEvents}, own detonation events {OwnEvents}, booms {Booms}, kicks {ShockKick.Count} (largest applied {ShockKick.MaxApplied:0.000} deg)";
    }

    /// Peak overpressure and positive phase of a TNT surface burst (hemispherical, i.e. on the ground: the reflection is in
    /// the fit). Kingery-Bulmash polynomials as simplified by Swisdak (1994), Z = R / W^(1/3) in m/kg^(1/3); beyond their range
    /// (Z > 198.5) the weak shock falls a little faster than 1/R.
    internal static class BlastPhysics
    {
        public static float PeakPa(float wKg, float rangeM)
        {
            float w3 = Mathf.Pow(Mathf.Max(wKg, 1e-4f), 1f / 3f);
            double z = Mathf.Max(rangeM, 0.05f) / w3;
            if (z > 198.5) return (float)(PeakKpa(198.5) * 1000.0 * Math.Pow(198.5 / z, 1.1));
            return (float)(PeakKpa(z) * 1000.0);
        }

        static double PeakKpa(double z)
        {
            double l;
            if (z < 2.9)
            {
                l = Math.Log(Math.Max(z, 0.2));
                return Math.Exp(7.2106 - 2.1069 * l - 0.3229 * l * l + 0.1117 * l * l * l + 0.0685 * l * l * l * l);
            }
            l = Math.Log(z);
            if (z < 23.8) return Math.Exp(7.5938 - 3.0523 * l + 0.40977 * l * l + 0.0261 * l * l * l - 0.01267 * l * l * l * l);
            return Math.Exp(6.0536 - 1.4066 * l);
        }

        /// Positive phase duration, s: ~4.8 ms per kg^(1/3) once the front has left the fireball (KB), lengthening slowly
        /// as a weak shock travels on (N-wave stretching).
        public static float PositiveSeconds(float wKg, float rangeM)
        {
            float w3 = Mathf.Pow(Mathf.Max(wKg, 1e-4f), 1f / 3f);
            float z = Mathf.Max(rangeM, 0.05f) / w3;
            float ms = 4.5f * (1f - Mathf.Exp(-z / 5f)) + 0.3f;
            if (z > 40f) ms *= Mathf.Sqrt(1f + 0.25f * Mathf.Log(z / 40f));
            return ms * w3 / 1000f;
        }

        /// Positive phase impulse, Pa*s (triangular pulse).
        public static float Impulse(float wKg, float rangeM) => 0.5f * PeakPa(wKg, rangeM) * PositiveSeconds(wKg, rangeM);

        public static float Db(float pa) => 20f * Mathf.Log10(Mathf.Max(pa, 1e-6f) / 20e-6f);
    }

    /// The low boom of a detonation, synthesised (no recordings): the N-wave of the blast's own length (positive phase
    /// ~ W^(1/3), so a rack's is several times a shell's), the ground and air column thumping at its fundamental, a handful
    /// of terrain echoes, and a rolling low rumble that lasts longer the bigger and farther the blast. Air absorption and the
    /// ground take the high end away with distance (two one-pole low-passes, cut-off falling with range).
    internal static class BoomSynth
    {
        /// 0 at the blast, 1 from ~2.5 km: how much of what you hear came back off the terrain rather than straight at you.
        public static float Far(float dist) => Mathf.Clamp01(Mathf.Log(1f + Mathf.Max(dist, 0f) / 120f) / Mathf.Log(1f + 2500f / 120f));

        /// Pure arithmetic (no Unity objects): runs on a worker thread.
        public static float[] Make(float w, float dist, int rate, int seed, float tailScale, out float nwaveMs, out float cutoffHz, out float tailS)
        {
            float w3 = Mathf.Pow(Mathf.Max(w, 0.01f), 1f / 3f);
            float T = Mathf.Clamp(2f * BlastPhysics.PositiveSeconds(w, dist), 0.004f, 0.09f);
            nwaveMs = T * 1000f;
            cutoffHz = Mathf.Clamp(16000f / (1f + dist / 80f), 110f, 16000f);
            // close: one huge bang and a short ring; far: the long rolling thunder of the echoes
            float far = Far(dist);
            tailS = Mathf.Clamp((0.5f + 3.2f * far + 0.2f * w3 * (0.4f + 0.6f * far)) * tailScale, 0.5f, 4.5f);
            // the direct blast falls off as 1/r, what comes back off the terrain has travelled hundreds of metres already:
            // near, echoes and rumble are far under the bang (~-22 dB at 20 m), at a kilometre they are most of it
            float refl = Mathf.Clamp(dist / (dist + 250f), 0.06f, 1f);
            int n = Mathf.CeilToInt((tailS + 0.35f) * rate);
            var x = new float[n];
            var rnd = new System.Random(seed);
            int i0 = rate / 100;    // 10 ms lead-in: the filters settle before the front
            NWave(x, i0, T, 1f, rate);
            // the thump: ground and air column ringing near the wave's fundamental (with its octave, so small speakers
            // still give the low note), short decay
            float fb = Mathf.Clamp(1.5f / T, 45f, 120f), tb = 0.06f + 0.03f * w3;
            for (int i = i0; i < n; i++)
            {
                float t = (i - i0) / (float)rate;
                if (t > tb * 8f) break;
                x[i] += (0.5f * Mathf.Sin(2f * Mathf.PI * fb * t) * Mathf.Exp(-t / tb) + 0.25f * Mathf.Sin(4f * Mathf.PI * fb * t) * Mathf.Exp(-t / (tb * 0.7f)))
                      * (1f - Mathf.Exp(-t / 0.003f));
            }
            // echoes off the terrain and tree lines: later, weaker, longer
            int echoes = 4 + rnd.Next(4);
            for (int e = 0; e < echoes; e++)
            {
                float dl = 0.08f + Mathf.Pow((float)rnd.NextDouble(), 1.4f) * tailS * 0.7f;
                float a = 0.5f * refl * Mathf.Exp(-dl / (tailS * 0.45f)) * (0.5f + 0.5f * (float)rnd.NextDouble());
                NWave(x, i0 + (int)(dl * rate), T * (1.3f + 0.8f * (float)rnd.NextDouble()), a, rate);
            }
            // the rolling rumble: low band noise swelling after the front, fading over the tail
            var rum = new float[n];
            for (int i = 0; i < n; i++) rum[i] = (float)rnd.NextDouble() * 2f - 1f;
            float rc = Mathf.Min(cutoffHz, 300f);
            LowPass(rum, rc, rate); LowPass(rum, rc, rate); HighPass(rum, 38f, rate); HighPass(rum, 38f, rate);
            double ss = 0;
            for (int i = 0; i < n; i++) ss += rum[i] * rum[i];
            float rms = (float)Math.Sqrt(ss / n) + 1e-6f;
            float rollF = 0.6f + 0.9f * (float)rnd.NextDouble(), ph = (float)rnd.NextDouble() * 6.283f;
            for (int i = i0; i < n; i++)
            {
                float t = (i - i0) / (float)rate;
                float env = (1f - Mathf.Exp(-t / 0.07f)) * Mathf.Exp(-t / (tailS * 0.33f));
                float roll = 0.65f + 0.35f * Mathf.Sin(2f * Mathf.PI * rollF * t + ph);
                x[i] += 0.32f * Mathf.Clamp(refl * 1.25f, 0.1f, 1f) * rum[i] / rms * env * roll;
            }
            LowPass(x, cutoffHz, rate); LowPass(x, cutoffHz, rate);
            HighPass(x, 30f, rate); HighPass(x, 30f, rate);     // nothing a speaker can't play eats the headroom
            float peak = 1e-6f;
            for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(x[i]));
            int fade = rate / 10;
            for (int i = 0; i < n; i++)
            {
                x[i] /= peak;
                if (i > n - fade) x[i] *= (n - i) / (float)fade;
            }
            return x;
        }

        static void NWave(float[] x, int start, float T, float amp, int rate)
        {
            int m = Mathf.Max(2, (int)(T * rate));
            for (int j = 0; j < m && start + j < x.Length; j++) x[start + j] += amp * (1f - 2f * j / m);
        }

        static void LowPass(float[] x, float fc, int rate)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * fc / rate), y = 0f;
            for (int i = 0; i < x.Length; i++) { y += a * (x[i] - y); x[i] = y; }
        }

        static void HighPass(float[] x, float fc, int rate)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * fc / rate), lo = 0f;
            for (int i = 0; i < x.Length; i++) { lo += a * (x[i] - lo); x[i] -= lo; }
        }
    }

    /// Plays the booms through Unity audio (2D, panned toward the blast). GHPC sets AudioListener.volume to its master
    /// volume and to 0 while paused or muted, so they follow the game's own volume.
    internal static class BoomPlayer
    {
        /// The boom's peak against the game's own explosion event for the same blast.
        const float BoomRel = 0.5f;

        class Voice { public AudioSource Src; public BoomMeter Meter; public float Until; public string Note; public bool Logged = true; }
        static readonly List<Voice> _voices = new List<Voice>();

        /// A boom being synthesised on a worker thread (15-45 ms of arithmetic: done in the frame, it was a hitch on every
        /// rack). Starts playing the frame it is ready (a frame or two after the front: nobody hears that).
        class Job
        {
            public float W, Dist, Amp, Mv, Pan, Queued;
            public int Rate, Seed;
            public bool Clipped;
            public float TailScale = 1f;
            public volatile float[] Data;
            public volatile string Error;
            public string Info;
            public double Ms;
        }
        static readonly List<Job> _jobs = new List<Job>();
        static GameObject _root;

        public static void Reset()
        {
            foreach (var v in _voices) if (v.Src != null) v.Src.Stop();
            _jobs.Clear();
        }

        static Voice Get()
        {
            if (_root == null)
            {
                _root = new GameObject("HitCam explosion booms") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(_root);
                _voices.Clear();
            }
            var v = _voices.FirstOrDefault(x => x.Src != null && !x.Src.isPlaying);
            if (v != null) return v;
            if (_voices.Count >= 6) return _voices.OrderBy(x => x.Until).First();
            var go = new GameObject("boom " + _voices.Count) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(_root.transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.priority = 0;
            src.bypassReverbZones = true;
            src.dopplerLevel = 0f;
            v = new Voice { Src = src, Meter = go.AddComponent<BoomMeter>() };
            _voices.Add(v);
            return v;
        }

        public static string Play(BlastSound.Det d, float w, float dist, float peakPa, Vector3 cam)
        {
            int rate = AudioSettings.outputSampleRate;
            if (rate < 8000) rate = 48000;
            // level: on the same physical scale as the game's events (the HE event's level at this distance times the
            // overpressure ratio), 6 dB under them; above the output ceiling (after the master volume) it is soft-clipped,
            // as an ear or a microphone saturates. A rack's follow-up blasts: a fraction of the first.
            float mv = Mathf.Max(AudioListener.volume, 0.05f);
            float amp = Mathf.Max(BoomRel * BlastSound.AnchorLevel(w, dist), 0.02f) * Mathf.Clamp(Settings.ExplosionLoudness.Value, 0.25f, 4f);
            var grade = BlastSound.Grade(d);
            amp *= grade.boom;
            amp = Mathf.Min(amp, 2.5f * 0.9f / mv);
            var c = GHPC.Camera.CameraManager.MainCam;
            Vector3 local = c != null ? c.transform.InverseTransformDirection(d.Pos - cam) : Vector3.forward;
            var job = new Job
            {
                W = w, Dist = dist, Amp = amp, Mv = mv, Rate = rate, Seed = (int)(d.T * 1000f) ^ d.Frame, TailScale = grade.tail,
                Clipped = amp * mv > 0.9f, Queued = Time.realtimeSinceStartup,
                Pan = local.sqrMagnitude > 1e-4f ? Mathf.Clamp(local.normalized.x, -1f, 1f) * 0.6f : 0f,
            };
            _jobs.Add(job);
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Synth(job));
            return $"synth queued, level {amp:0.00} of full scale before the master volume{(job.Clipped ? " (soft-clipped to 0.9 at the output)" : "")}, x{grade.boom:0.00} and tail x{grade.tail:0.0} for this kind of blast, pan {job.Pan:+0.00;-0.00}";
        }

        /// Worker thread: arithmetic only.
        static void Synth(Job j)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var data = BoomSynth.Make(j.W, j.Dist, j.Rate, j.Seed, j.TailScale, out float nMs, out float fc, out float tail);
                if (j.Clipped)
                {
                    float drive = j.Amp * j.Mv / 0.9f, norm = (float)Math.Tanh(drive);
                    for (int i = 0; i < data.Length; i++) data[i] = (float)Math.Tanh(data[i] * drive) / norm * 0.9f / j.Mv;
                }
                else for (int i = 0; i < data.Length; i++) data[i] *= j.Amp;
                j.Ms = sw.Elapsed.TotalMilliseconds;
                j.Info = $"N-wave {nMs:0} ms, low-pass {fc:0} Hz, tail {tail:0.0} s";
                j.Data = data;      // last: hands it to the main thread
            }
            catch (Exception e) { j.Error = e.Message; }
        }

        static void Start(Job j)
        {
            var data = j.Data;
            var clip = AudioClip.Create("hitcam_boom", data.Length, 1, j.Rate, false);
            clip.SetData(data, 0);
            var v = Get();
            var old = v.Src.clip;
            v.Src.Stop();
            if (old != null) UnityEngine.Object.Destroy(old);
            v.Src.clip = clip;
            v.Src.panStereo = j.Pan;
            v.Src.volume = 1f;
            v.Meter.Peak = 0f;
            LastPeak = 0f;
            v.Src.Play();
            v.Until = Time.unscaledTime + clip.length + 0.5f;
            v.Note = $"{clip.length:0.0} s boom at {j.Dist:0} m ({j.Info}; made in {j.Ms:0} ms on a worker, playing {(Time.realtimeSinceStartup - j.Queued) * 1000f:0} ms after the front)";
            v.Logged = !AutoTest.Active;
            BlastSound.Booms++;
        }

        public static float LastPeak;

        public static void Update()
        {
            for (int i = 0; i < _jobs.Count; i++)
            {
                var j = _jobs[i];
                if (j.Error != null) { HitCamMod.Log.Warning("Explosion sound: boom synth failed: " + j.Error); _jobs.RemoveAt(i--); }
                else if (j.Data != null)
                {
                    _jobs.RemoveAt(i--);
                    try { Start(j); } catch (Exception e) { HitCamMod.Log.Warning("Explosion sound: boom: " + e.Message); }
                }
                else if (Time.realtimeSinceStartup - j.Queued > 3f) _jobs.RemoveAt(i--);   // never finished: drop it
            }
            foreach (var v in _voices)
            {
                if (v.Src != null && v.Src.isPlaying) LastPeak = Mathf.Max(LastPeak, v.Meter.Peak);
                if (v.Logged || v.Src == null || v.Src.isPlaying) continue;
                v.Logged = true;
                HitCamMod.Log.Msg($"Explosion sound: {v.Note} played, output peak {v.Meter.Peak:0.000} (AudioListener volume {AudioListener.volume:0.00})");
            }
        }
    }

    /// Records the loudest sample a boom source put out (self-test proof that it reached the mix).
    internal class BoomMeter : MonoBehaviour
    {
        public volatile float Peak;

        void OnAudioFilterRead(float[] data, int channels)
        {
            float m = Peak;
            for (int i = 0; i < data.Length; i++) { float a = data[i] < 0f ? -data[i] : data[i]; if (a > m) m = a; }
            Peak = m;
        }
    }

    internal static partial class Settings
    {
        public static MelonPreferences_Entry<bool> ExplosionSound;
        public static MelonPreferences_Entry<float> ExplosionLoudness;
        public static MelonPreferences_Entry<bool> ShockwaveShake;

        internal static void InitBlast()
        {
            ExplosionSound = Cat.CreateEntry("ExplosionSound", true, description: "Explosions as loud as their size: an ammo rack going off is a huge boom you hear from far away, arriving at the speed of sound. A vehicle's later blasts are quieter. Off: the game's own sounds.");
            ExplosionLoudness = Cat.CreateEntry("ExplosionLoudness", 1f, description: "Extra volume for the explosions on top of that (1 = as computed).");
            ShockwaveShake = Cat.CreateEntry("ShockwaveShake", true, description: "A short camera kick when a blast wave reaches you.");
        }
    }
}
